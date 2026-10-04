using System.Reflection;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Zombies.Engine.Platform;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Zombies.Engine.Render.Vulkan;

public sealed class VulkanException(string message, Result result) : Exception($"{message} ({result})")
{
    public Result Result { get; } = result;
}

/// <summary>
/// Draws sprite batches with Vulkan 1.1. It uses a plain render pass rather than newer dynamic rendering so it runs on the
/// oldest hardware we support. Frame resources are doubled so the CPU can record one frame while the GPU draws the previous one.
/// </summary>
public sealed unsafe class VulkanRenderer : IRenderer
{
    private const int FramesInFlight = 2;
    private const uint MaxVertices = SpriteBatch.MaxQuads * 4;
    private const uint MaxIndices = SpriteBatch.MaxQuads * 6;

    private readonly Vk _vk = Vk.GetApi();
    private readonly SdlWindow _window;
    private readonly RendererOptions _options;

    private Instance _instance;
    private SurfaceKHR _surface;
    private PhysicalDevice _physical;
    private Device _device;
    private Queue _queue;
    private uint _queueFamily;
    private KhrSurface _surfaceExt = null!;
    private KhrSwapchain _swapchainExt = null!;
    private string _deviceName = string.Empty;

    private SwapchainKHR _swapchain;
    private Format _format;
    private Extent2D _extent;
    private Image[] _images = [];
    private ImageView[] _views = [];
    private Framebuffer[] _framebuffers = [];
    private Silk.NET.Vulkan.Semaphore[] _renderFinished = [];
    private bool _canCapture;
    private RenderPass _renderPass;

    private CommandPool _pool;
    private readonly CommandBuffer[] _commands = new CommandBuffer[FramesInFlight];
    private readonly Silk.NET.Vulkan.Semaphore[] _imageAvailable = new Silk.NET.Vulkan.Semaphore[FramesInFlight];
    private readonly Fence[] _fences = new Fence[FramesInFlight];
    private readonly Buffer[] _vertexBuffers = new Buffer[FramesInFlight];
    private readonly DeviceMemory[] _vertexMemory = new DeviceMemory[FramesInFlight];
    private readonly nint[] _vertexMapped = new nint[FramesInFlight];
    private Buffer _indexBuffer;
    private DeviceMemory _indexMemory;

    private Image _atlas;
    private DeviceMemory _atlasMemory;
    private ImageView _atlasView;
    private Sampler _sampler;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;

    private int _frame;
    private int _width;
    private int _height;
    private bool _needsRecreate;
    private string? _capturePath;
    private bool _disposed;

    public VulkanRenderer(SdlWindow window, RendererOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _options = options == default ? new RendererOptions() : options;
        _width = window.PixelWidth;
        _height = window.PixelHeight;

        try
        {
            CreateInstance();
            _surface = new SurfaceKHR(window.CreateVulkanSurface(_instance.Handle));
            PickPhysicalDevice();
            CreateDevice();
            CreateRenderPassAndSwapchain();
            CreateFrameResources();
            CreateSpriteBuffers();
            CreateAtlas();
            CreatePipeline();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string DeviceName => _deviceName;

    /// <summary>The present mode and swapchain size in use, for diagnostics, for example "Fifo, 3 images".</summary>
    public string PresentationInfo { get; private set; } = "none";

    public int Width => _width;

    public int Height => _height;

    public void Resize(int width, int height)
    {
        if (width == _width && height == _height)
        {
            return;
        }

        _width = width;
        _height = height;
        _needsRecreate = true;
    }

    public void RequestCapture(string pngPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(pngPath);
        _capturePath = pngPath;
    }

    public bool Render(SpriteBatch sprites, Rgba clear)
    {
        ArgumentNullException.ThrowIfNull(sprites);
        if (_width <= 0 || _height <= 0)
        {
            return false;
        }

        if (_needsRecreate || _swapchain.Handle == 0)
        {
            if (!RebuildSwapchain())
            {
                return false;
            }
        }

        var fence = _fences[_frame];
        Check(_vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue), "waiting for the previous frame");

        uint imageIndex;
        var acquire = _swapchainExt.AcquireNextImage(_device, _swapchain, ulong.MaxValue, _imageAvailable[_frame], default, &imageIndex);
        if (acquire == Result.ErrorOutOfDateKhr)
        {
            _needsRecreate = true;
            return false;
        }

        if (acquire is not (Result.Success or Result.SuboptimalKhr))
        {
            throw new VulkanException("Could not acquire a swapchain image", acquire);
        }

        Check(_vk.ResetFences(_device, 1, &fence), "resetting the frame fence");

        var vertices = sprites.Vertices;
        var count = (int)Math.Min((uint)vertices.Length, MaxVertices) / 4 * 4;
        if (count > 0)
        {
            fixed (SpriteVertex* source = vertices)
            {
                System.Buffer.MemoryCopy(source, (void*)_vertexMapped[_frame], MaxVertices * (uint)sizeof(SpriteVertex), (uint)count * (uint)sizeof(SpriteVertex));
            }
        }

        var capture = _capturePath;
        var capturing = capture is not null && _canCapture;
        RecordFrame(_commands[_frame], imageIndex, (uint)(count / 4 * 6), clear, out var captureBuffer, capturing, out var captureMemory);

        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        var available = _imageAvailable[_frame];
        var finished = _renderFinished[imageIndex];
        var commandBuffer = _commands[_frame];
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &available,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &finished,
        };
        Check(_vk.QueueSubmit(_queue, 1, &submit, fence), "submitting the frame");

        if (capturing)
        {
            Check(_vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue), "waiting for the capture");
            SaveCapture(capture!, captureBuffer, captureMemory);
            _capturePath = null;
        }

        var swapchain = _swapchain;
        var present = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &finished,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };
        var presented = _swapchainExt.QueuePresent(_queue, &present);
        if (presented is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
        {
            _needsRecreate = true;
        }
        else if (presented != Result.Success)
        {
            throw new VulkanException("Could not present the frame", presented);
        }

        _frame = (_frame + 1) % FramesInFlight;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_device.Handle != 0)
        {
            _vk.DeviceWaitIdle(_device);
        }

        if (_device.Handle != 0)
        {
            _vk.DestroyPipeline(_device, _pipeline, null);
            _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
            _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
            _vk.DestroyDescriptorSetLayout(_device, _setLayout, null);
            _vk.DestroySampler(_device, _sampler, null);
            _vk.DestroyImageView(_device, _atlasView, null);
            _vk.DestroyImage(_device, _atlas, null);
            _vk.FreeMemory(_device, _atlasMemory, null);
            _vk.DestroyBuffer(_device, _indexBuffer, null);
            _vk.FreeMemory(_device, _indexMemory, null);

            for (var i = 0; i < FramesInFlight; i++)
            {
                if (_vertexMapped[i] != 0)
                {
                    _vk.UnmapMemory(_device, _vertexMemory[i]);
                }

                _vk.DestroyBuffer(_device, _vertexBuffers[i], null);
                _vk.FreeMemory(_device, _vertexMemory[i], null);
                _vk.DestroySemaphore(_device, _imageAvailable[i], null);
                _vk.DestroyFence(_device, _fences[i], null);
            }

            _vk.DestroyCommandPool(_device, _pool, null);
            DestroySwapchain();
            _vk.DestroyRenderPass(_device, _renderPass, null);
            _vk.DestroyDevice(_device, null);
        }

        if (_surface.Handle != 0)
        {
            SdlWindow.DestroyVulkanSurface(_instance.Handle, _surface.Handle);
        }

        if (_instance.Handle != 0)
        {
            _vk.DestroyInstance(_instance, null);
        }

        _vk.Dispose();
    }

    private static void Check(Result result, string what)
    {
        if (result != Result.Success)
        {
            throw new VulkanException($"Vulkan failed while {what}", result);
        }
    }

    private void CreateInstance()
    {
        var extensions = SdlWindow.VulkanInstanceExtensions();
        var application = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)SilkMarshal.StringToPtr("Zombies.NET"),
            ApplicationVersion = Vk.MakeVersion(0, 1, 0),
            PEngineName = (byte*)SilkMarshal.StringToPtr("Zombies.Engine"),
            EngineVersion = Vk.MakeVersion(0, 1, 0),
            ApiVersion = Vk.Version11,
        };

        var layers = ValidationLayers();
        var extensionNames = SilkMarshal.StringArrayToPtr(extensions);
        var layerNames = layers.Length > 0 ? SilkMarshal.StringArrayToPtr(layers) : 0;
        try
        {
            var info = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &application,
                EnabledExtensionCount = (uint)extensions.Length,
                PpEnabledExtensionNames = (byte**)extensionNames,
                EnabledLayerCount = (uint)layers.Length,
                PpEnabledLayerNames = (byte**)layerNames,
            };
            Instance instance;
            Check(_vk.CreateInstance(&info, null, &instance), "creating the Vulkan instance");
            _instance = instance;
        }
        finally
        {
            SilkMarshal.Free(extensionNames);
            if (layerNames != 0)
            {
                SilkMarshal.Free(layerNames);
            }

            SilkMarshal.Free((nint)application.PApplicationName);
            SilkMarshal.Free((nint)application.PEngineName);
        }

        _vk.TryGetInstanceExtension(_instance, out _surfaceExt);
    }

    /// <summary>The Khronos validation layer, but only when asked for with ZOMBIES_VK_VALIDATION=1 and actually installed.</summary>
    private string[] ValidationLayers()
    {
        if (Environment.GetEnvironmentVariable("ZOMBIES_VK_VALIDATION") != "1")
        {
            return [];
        }

        uint count = 0;
        _vk.EnumerateInstanceLayerProperties(&count, null);
        var properties = new LayerProperties[count];
        fixed (LayerProperties* p = properties)
        {
            _vk.EnumerateInstanceLayerProperties(&count, p);
        }

        const string name = "VK_LAYER_KHRONOS_validation";
        foreach (var layer in properties)
        {
            var layerName = layer.LayerName;
            if (Marshal.PtrToStringAnsi((nint)layerName) == name)
            {
                return [name];
            }
        }

        return [];
    }

    private void PickPhysicalDevice()
    {
        uint count = 0;
        Check(_vk.EnumeratePhysicalDevices(_instance, &count, null), "listing graphics devices");
        if (count == 0)
        {
            throw new InvalidOperationException("No Vulkan graphics device was found.");
        }

        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* d = devices)
        {
            Check(_vk.EnumeratePhysicalDevices(_instance, &count, d), "listing graphics devices");
        }

        var bestScore = -1;
        foreach (var device in devices)
        {
            var family = FindQueueFamily(device);
            if (family < 0 || !SupportsSwapchain(device))
            {
                continue;
            }

            _vk.GetPhysicalDeviceProperties(device, out var properties);
            if (properties.ApiVersion < Vk.Version11)
            {
                continue;
            }

            var score = properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 1000,
                PhysicalDeviceType.IntegratedGpu => 500,
                _ => 100,
            };

            if (score > bestScore)
            {
                bestScore = score;
                _physical = device;
                _queueFamily = (uint)family;
                _deviceName = Marshal.PtrToStringAnsi((nint)properties.DeviceName) ?? "Unknown GPU";
            }
        }

        if (bestScore < 0)
        {
            throw new InvalidOperationException("No Vulkan 1.1 device that can draw to the window was found.");
        }
    }

    private int FindQueueFamily(PhysicalDevice device)
    {
        uint count = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* f = families)
        {
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, f);
        }

        for (uint i = 0; i < count; i++)
        {
            if ((families[i].QueueFlags & QueueFlags.GraphicsBit) == 0)
            {
                continue;
            }

            Bool32 supported;
            _surfaceExt.GetPhysicalDeviceSurfaceSupport(device, i, _surface, &supported);
            if (supported)
            {
                return (int)i;
            }
        }

        return -1;
    }

    private bool SupportsSwapchain(PhysicalDevice device)
    {
        uint count = 0;
        _vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, null);
        var extensions = new ExtensionProperties[count];
        fixed (ExtensionProperties* e = extensions)
        {
            _vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, e);
        }

        foreach (var extension in extensions)
        {
            var name = extension.ExtensionName;
            if (Marshal.PtrToStringAnsi((nint)name) == KhrSwapchain.ExtensionName)
            {
                return true;
            }
        }

        return false;
    }

    private void CreateDevice()
    {
        var priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _queueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        var extensionNames = SilkMarshal.StringArrayToPtr([KhrSwapchain.ExtensionName]);
        try
        {
            var features = default(PhysicalDeviceFeatures);
            var info = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueInfo,
                EnabledExtensionCount = 1,
                PpEnabledExtensionNames = (byte**)extensionNames,
                PEnabledFeatures = &features,
            };
            Device device;
            Check(_vk.CreateDevice(_physical, &info, null, &device), "creating the logical device");
            _device = device;
        }
        finally
        {
            SilkMarshal.Free(extensionNames);
        }

        _vk.GetDeviceQueue(_device, _queueFamily, 0, out _queue);
        _vk.TryGetDeviceExtension(_instance, _device, out _swapchainExt);
    }

    private void CreateRenderPassAndSwapchain()
    {
        // The swapchain format decides the render pass, so look it up first and create the pass once.
        _format = ChooseSurfaceFormat().Format;

        var color = new AttachmentDescription
        {
            Format = _format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };
        var reference = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &reference,
        };
        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &color,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };
        RenderPass pass;
        Check(_vk.CreateRenderPass(_device, &info, null, &pass), "creating the render pass");
        _renderPass = pass;

        CreateSwapchain();
    }

    private SurfaceFormatKHR ChooseSurfaceFormat()
    {
        uint count = 0;
        _surfaceExt.GetPhysicalDeviceSurfaceFormats(_physical, _surface, &count, null);
        var formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* f = formats)
        {
            _surfaceExt.GetPhysicalDeviceSurfaceFormats(_physical, _surface, &count, f);
        }

        foreach (var format in formats)
        {
            if (format.Format == Format.B8G8R8A8Unorm && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                return format;
            }
        }

        return formats[0];
    }

    private PresentModeKHR ChoosePresentMode()
    {
        if (_options.VSync)
        {
            return PresentModeKHR.FifoKhr;
        }

        uint count = 0;
        _surfaceExt.GetPhysicalDeviceSurfacePresentModes(_physical, _surface, &count, null);
        var modes = new PresentModeKHR[count];
        fixed (PresentModeKHR* m = modes)
        {
            _surfaceExt.GetPhysicalDeviceSurfacePresentModes(_physical, _surface, &count, m);
        }

        foreach (var wanted in new[] { PresentModeKHR.ImmediateKhr, PresentModeKHR.MailboxKhr })
        {
            if (modes.Contains(wanted))
            {
                return wanted;
            }
        }

        return PresentModeKHR.FifoKhr;
    }

    /// <summary>Creates the swapchain, its image views and framebuffers. Does nothing while the window has no drawable area.</summary>
    private void CreateSwapchain()
    {
        _surfaceExt.GetPhysicalDeviceSurfaceCapabilities(_physical, _surface, out var capabilities);
        var extent = capabilities.CurrentExtent;
        if (extent.Width == uint.MaxValue)
        {
            extent = new Extent2D(
                Math.Clamp((uint)_width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Math.Clamp((uint)_height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));
        }

        if (extent.Width == 0 || extent.Height == 0)
        {
            _swapchain = default;
            return;
        }

        var imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
        {
            imageCount = capabilities.MaxImageCount;
        }

        _canCapture = (capabilities.SupportedUsageFlags & ImageUsageFlags.TransferSrcBit) != 0;
        var surfaceFormat = ChooseSurfaceFormat();
        var presentMode = ChoosePresentMode();
        var info = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | (_canCapture ? ImageUsageFlags.TransferSrcBit : 0),
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
        };
        SwapchainKHR swapchain;
        Check(_swapchainExt.CreateSwapchain(_device, &info, null, &swapchain), "creating the swapchain");
        _swapchain = swapchain;
        _extent = extent;
        _format = surfaceFormat.Format;
        PresentationInfo = $"{presentMode}, requested {imageCount} images";

        uint count = 0;
        _swapchainExt.GetSwapchainImages(_device, _swapchain, &count, null);
        _images = new Image[count];
        fixed (Image* i = _images)
        {
            _swapchainExt.GetSwapchainImages(_device, _swapchain, &count, i);
        }

        _views = new ImageView[count];
        _framebuffers = new Framebuffer[count];
        _renderFinished = new Silk.NET.Vulkan.Semaphore[count];
        for (var n = 0; n < count; n++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _images[n],
                ViewType = ImageViewType.Type2D,
                Format = _format,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            ImageView view;
            Check(_vk.CreateImageView(_device, &viewInfo, null, &view), "creating a swapchain image view");
            _views[n] = view;

            var attachment = view;
            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 1,
                PAttachments = &attachment,
                Width = extent.Width,
                Height = extent.Height,
                Layers = 1,
            };
            Framebuffer framebuffer;
            Check(_vk.CreateFramebuffer(_device, &framebufferInfo, null, &framebuffer), "creating a framebuffer");
            _framebuffers[n] = framebuffer;

            var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            Silk.NET.Vulkan.Semaphore semaphore;
            Check(_vk.CreateSemaphore(_device, &semaphoreInfo, null, &semaphore), "creating a semaphore");
            _renderFinished[n] = semaphore;
        }
    }

    private void DestroySwapchain()
    {
        foreach (var semaphore in _renderFinished)
        {
            _vk.DestroySemaphore(_device, semaphore, null);
        }

        foreach (var framebuffer in _framebuffers)
        {
            _vk.DestroyFramebuffer(_device, framebuffer, null);
        }

        foreach (var view in _views)
        {
            _vk.DestroyImageView(_device, view, null);
        }

        if (_swapchain.Handle != 0)
        {
            _swapchainExt.DestroySwapchain(_device, _swapchain, null);
        }

        _renderFinished = [];
        _framebuffers = [];
        _views = [];
        _images = [];
        _swapchain = default;
    }

    /// <summary>Waits for the GPU, then rebuilds the swapchain at the current size. Returns false if there is nothing to draw into yet.</summary>
    private bool RebuildSwapchain()
    {
        _vk.DeviceWaitIdle(_device);
        DestroySwapchain();
        CreateSwapchain();
        _needsRecreate = false;
        return _swapchain.Handle != 0;
    }

    private void CreateFrameResources()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _queueFamily,
        };
        CommandPool pool;
        Check(_vk.CreateCommandPool(_device, &poolInfo, null, &pool), "creating the command pool");
        _pool = pool;

        var allocate = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = FramesInFlight,
        };
        fixed (CommandBuffer* buffers = _commands)
        {
            Check(_vk.AllocateCommandBuffers(_device, &allocate, buffers), "allocating command buffers");
        }

        for (var i = 0; i < FramesInFlight; i++)
        {
            var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            Silk.NET.Vulkan.Semaphore semaphore;
            Check(_vk.CreateSemaphore(_device, &semaphoreInfo, null, &semaphore), "creating a semaphore");
            _imageAvailable[i] = semaphore;

            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
            Fence fence;
            Check(_vk.CreateFence(_device, &fenceInfo, null, &fence), "creating a fence");
            _fences[i] = fence;
        }
    }

    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags wanted)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physical, out var memory);
        for (var i = 0; i < memory.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << i)) != 0 && (memory.MemoryTypes[i].PropertyFlags & wanted) == wanted)
            {
                return (uint)i;
            }
        }

        throw new InvalidOperationException("No suitable graphics memory type was found.");
    }

    private void CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties, out Buffer buffer, out DeviceMemory memory)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        Buffer created;
        Check(_vk.CreateBuffer(_device, &info, null, &created), "creating a buffer");
        buffer = created;

        _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, properties),
        };
        DeviceMemory allocated;
        Check(_vk.AllocateMemory(_device, &allocate, null, &allocated), "allocating buffer memory");
        memory = allocated;
        Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "binding buffer memory");
    }

    private void CreateSpriteBuffers()
    {
        const MemoryPropertyFlags visible = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        for (var i = 0; i < FramesInFlight; i++)
        {
            CreateBuffer(MaxVertices * (ulong)sizeof(SpriteVertex), BufferUsageFlags.VertexBufferBit, visible, out _vertexBuffers[i], out _vertexMemory[i]);
            void* mapped;
            Check(_vk.MapMemory(_device, _vertexMemory[i], 0, Vk.WholeSize, 0, &mapped), "mapping the vertex buffer");
            _vertexMapped[i] = (nint)mapped;
        }

        // Every quad uses the same four-corner pattern, so the index buffer is filled once.
        CreateBuffer(MaxIndices * sizeof(ushort), BufferUsageFlags.IndexBufferBit, visible, out _indexBuffer, out _indexMemory);
        void* data;
        Check(_vk.MapMemory(_device, _indexMemory, 0, Vk.WholeSize, 0, &data), "mapping the index buffer");
        var indices = (ushort*)data;
        for (var quad = 0; quad < SpriteBatch.MaxQuads; quad++)
        {
            var v = (ushort)(quad * 4);
            var i = quad * 6;
            indices[i] = v;
            indices[i + 1] = (ushort)(v + 1);
            indices[i + 2] = (ushort)(v + 2);
            indices[i + 3] = v;
            indices[i + 4] = (ushort)(v + 2);
            indices[i + 5] = (ushort)(v + 3);
        }

        _vk.UnmapMemory(_device, _indexMemory);
    }

    /// <summary>Uploads the debug font atlas as a one-channel texture and builds the sampler and descriptor that expose it to the shader.</summary>
    private void CreateAtlas()
    {
        var pixels = DebugFont.CreateAtlas();
        var width = (uint)DebugFont.AtlasWidth;
        var height = (uint)DebugFont.AtlasHeight;

        CreateBuffer((ulong)pixels.Length, BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out var staging, out var stagingMemory);
        void* mapped;
        Check(_vk.MapMemory(_device, stagingMemory, 0, Vk.WholeSize, 0, &mapped), "mapping the staging buffer");
        pixels.AsSpan().CopyTo(new Span<byte>(mapped, pixels.Length));
        _vk.UnmapMemory(_device, stagingMemory);

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.R8Unorm,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Image atlas;
        Check(_vk.CreateImage(_device, &imageInfo, null, &atlas), "creating the atlas image");
        _atlas = atlas;

        _vk.GetImageMemoryRequirements(_device, _atlas, out var requirements);
        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        DeviceMemory memory;
        Check(_vk.AllocateMemory(_device, &allocate, null, &memory), "allocating atlas memory");
        _atlasMemory = memory;
        Check(_vk.BindImageMemory(_device, _atlas, _atlasMemory, 0), "binding atlas memory");

        var cmd = BeginOneShot();
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        Barrier(cmd, _atlas, range, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(width, height, 1),
        };
        _vk.CmdCopyBufferToImage(cmd, staging, _atlas, ImageLayout.TransferDstOptimal, 1, &region);
        Barrier(cmd, _atlas, range, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit);
        EndOneShot(cmd);

        _vk.DestroyBuffer(_device, staging, null);
        _vk.FreeMemory(_device, stagingMemory, null);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _atlas,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8Unorm,
            SubresourceRange = range,
        };
        ImageView view;
        Check(_vk.CreateImageView(_device, &viewInfo, null, &view), "creating the atlas view");
        _atlasView = view;

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        Sampler sampler;
        Check(_vk.CreateSampler(_device, &samplerInfo, null, &sampler), "creating the sampler");
        _sampler = sampler;

        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };
        DescriptorSetLayout layout;
        Check(_vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, &layout), "creating the descriptor layout");
        _setLayout = layout;

        var poolSize = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = 1,
        };
        DescriptorPool pool;
        Check(_vk.CreateDescriptorPool(_device, &poolInfo, null, &pool), "creating the descriptor pool");
        _descriptorPool = pool;

        var setLayout = _setLayout;
        var setInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        DescriptorSet set;
        Check(_vk.AllocateDescriptorSets(_device, &setInfo, &set), "allocating the descriptor set");
        _descriptorSet = set;

        var imageDescriptor = new DescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = _atlasView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &imageDescriptor,
        };
        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
    }

    private CommandBuffer BeginOneShot()
    {
        var allocate = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CommandBuffer cmd;
        Check(_vk.AllocateCommandBuffers(_device, &allocate, &cmd), "allocating a one-time command buffer");
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Check(_vk.BeginCommandBuffer(cmd, &begin), "starting a one-time command buffer");
        return cmd;
    }

    private void EndOneShot(CommandBuffer cmd)
    {
        Check(_vk.EndCommandBuffer(cmd), "ending a one-time command buffer");
        var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
        Check(_vk.QueueSubmit(_queue, 1, &submit, default), "submitting a one-time command buffer");
        Check(_vk.QueueWaitIdle(_queue), "waiting for a one-time command buffer");
        _vk.FreeCommandBuffers(_device, _pool, 1, &cmd);
    }

    private void Barrier(CommandBuffer cmd, Image image, ImageSubresourceRange range, ImageLayout from, ImageLayout to, AccessFlags srcAccess, AccessFlags dstAccess, PipelineStageFlags srcStage, PipelineStageFlags dstStage)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };
        _vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    private ShaderModule CreateShader(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The compiled shader '{resource}' is missing from the build.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        var words = new uint[bytes.Length / 4];
        System.Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);

        fixed (uint* code = words)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)bytes.Length,
                PCode = code,
            };
            ShaderModule module;
            Check(_vk.CreateShaderModule(_device, &info, null, &module), $"creating the shader module {resource}");
            return module;
        }
    }

    private void CreatePipeline()
    {
        var vertexShader = CreateShader("sprite.vert.spv");
        var fragmentShader = CreateShader("sprite.frag.spv");
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertexShader, PName = entry };
            stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragmentShader, PName = entry };

            var binding = new VertexInputBindingDescription { Binding = 0, Stride = (uint)sizeof(SpriteVertex), InputRate = VertexInputRate.Vertex };
            var attributes = stackalloc VertexInputAttributeDescription[3];
            attributes[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32Sfloat, Offset = 0 };
            attributes[1] = new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R32G32Sfloat, Offset = 8 };
            attributes[2] = new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = Format.R8G8B8A8Unorm, Offset = 16 };
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 3,
                PVertexAttributeDescriptions = attributes,
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1,
            };
            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment,
            };
            var dynamicStates = stackalloc DynamicState[2];
            dynamicStates[0] = DynamicState.Viewport;
            dynamicStates[1] = DynamicState.Scissor;
            var dynamic = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates,
            };

            var setLayout = _setLayout;
            var pushRange = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit, Offset = 0, Size = 8 };
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &setLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushRange,
            };
            PipelineLayout layout;
            Check(_vk.CreatePipelineLayout(_device, &layoutInfo, null, &layout), "creating the pipeline layout");
            _pipelineLayout = layout;

            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisample,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = _pipelineLayout,
                RenderPass = _renderPass,
                Subpass = 0,
            };
            Pipeline pipeline;
            Check(_vk.CreateGraphicsPipelines(_device, default, 1, &info, null, &pipeline), "creating the graphics pipeline");
            _pipeline = pipeline;
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
            _vk.DestroyShaderModule(_device, vertexShader, null);
            _vk.DestroyShaderModule(_device, fragmentShader, null);
        }
    }

    private void RecordFrame(CommandBuffer cmd, uint imageIndex, uint indexCount, Rgba clear, out Buffer captureBuffer, bool capturing, out DeviceMemory captureMemory)
    {
        captureBuffer = default;
        captureMemory = default;

        Check(_vk.ResetCommandBuffer(cmd, 0), "resetting the command buffer");
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Check(_vk.BeginCommandBuffer(cmd, &begin), "recording the frame");

        var clearValue = new ClearValue { Color = new ClearColorValue(clear.R / 255f, clear.G / 255f, clear.B / 255f, clear.A / 255f) };
        var passInfo = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffers[imageIndex],
            RenderArea = new Rect2D(new Offset2D(0, 0), _extent),
            ClearValueCount = 1,
            PClearValues = &clearValue,
        };
        _vk.CmdBeginRenderPass(cmd, &passInfo, SubpassContents.Inline);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);

        var viewport = new Viewport(0, 0, _extent.Width, _extent.Height, 0, 1);
        var scissor = new Rect2D(new Offset2D(0, 0), _extent);
        _vk.CmdSetViewport(cmd, 0, 1, &viewport);
        _vk.CmdSetScissor(cmd, 0, 1, &scissor);

        var set = _descriptorSet;
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &set, 0, null);
        var screen = stackalloc float[2];
        screen[0] = _extent.Width;
        screen[1] = _extent.Height;
        _vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit, 0, 8, screen);

        if (indexCount > 0)
        {
            var vertexBuffer = _vertexBuffers[_frame];
            ulong offset = 0;
            _vk.CmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
            _vk.CmdBindIndexBuffer(cmd, _indexBuffer, 0, IndexType.Uint16);
            _vk.CmdDrawIndexed(cmd, indexCount, 1, 0, 0, 0);
        }

        _vk.CmdEndRenderPass(cmd);

        if (capturing)
        {
            var size = (ulong)_extent.Width * _extent.Height * 4;
            CreateBuffer(size, BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out captureBuffer, out captureMemory);

            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            var image = _images[imageIndex];
            Barrier(cmd, image, range, ImageLayout.PresentSrcKhr, ImageLayout.TransferSrcOptimal, AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(_extent.Width, _extent.Height, 1),
            };
            _vk.CmdCopyImageToBuffer(cmd, image, ImageLayout.TransferSrcOptimal, captureBuffer, 1, &region);
            Barrier(cmd, image, range, ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr, AccessFlags.TransferReadBit, 0, PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit);
        }

        Check(_vk.EndCommandBuffer(cmd), "finishing the frame");
    }

    private void SaveCapture(string path, Buffer buffer, DeviceMemory memory)
    {
        var width = (int)_extent.Width;
        var height = (int)_extent.Height;
        var length = width * height * 4;

        void* mapped;
        Check(_vk.MapMemory(_device, memory, 0, Vk.WholeSize, 0, &mapped), "reading back the frame");
        var rgba = new byte[length];
        new ReadOnlySpan<byte>(mapped, length).CopyTo(rgba);
        _vk.UnmapMemory(_device, memory);
        _vk.DestroyBuffer(_device, buffer, null);
        _vk.FreeMemory(_device, memory, null);

        if (_format is Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb)
        {
            for (var i = 0; i < length; i += 4)
            {
                (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
            }
        }

        PngWriter.Write(path, width, height, rgba);
    }
}
