using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Zombies.Engine.Voxel;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Zombies.Engine.Render.Vulkan;

/// <summary>Per-frame values the terrain shaders read. Field order and sizes must match the cbuffer in the HLSL.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WorldUniforms
{
    public Matrix4x4 ViewProjection;
    public Matrix4x4 Cascade0;
    public Matrix4x4 Cascade1;
    public Matrix4x4 Cascade2;
    public Vector4 CascadeSplits;
    public Vector4 SunDirection;
    public Vector4 SunColor;
    public Vector4 AmbientColor;
    public Vector4 FogColor;
    public Vector4 CameraPosition;
    public Vector4 ShadowParams;
}

/// <summary>
/// The world half of the Vulkan renderer: chunk meshes in two big shared buffers, a shadow map per cascade, and the terrain pass.
/// Chunk memory is sub-allocated from the arenas because Vulkan allows only a few thousand separate allocations.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private const Format DepthFormat = Format.D32Sfloat;

    /// <summary>Vertices and indices each chunk section may use in total. The arenas hold all loaded chunks at once.</summary>
    private const int ArenaVertexCapacity = 3_000_000;
    private const int ArenaIndexCapacity = 6_000_000;

    private readonly RangeAllocator _vertexArena = new(ArenaVertexCapacity);
    private readonly RangeAllocator _indexArena = new(ArenaIndexCapacity);
    private readonly Queue<(long Frame, GpuChunk Chunk)> _pendingReleases = new();
    private readonly List<DrawItem> _visible = [];
    private readonly List<DrawItem>[] _shadowVisible = [[], [], []];
    private readonly Buffer[] _uniformBuffers = new Buffer[FramesInFlight];
    private readonly DeviceMemory[] _uniformMemory = new DeviceMemory[FramesInFlight];
    private readonly nint[] _uniformMapped = new nint[FramesInFlight];
    private readonly DescriptorSet[] _worldSets = new DescriptorSet[FramesInFlight];
    private readonly Framebuffer[] _shadowFramebuffers = new Framebuffer[ShadowSettings.MaxCascades];
    private readonly ImageView[] _shadowLayerViews = new ImageView[ShadowSettings.MaxCascades];

    private Buffer _vertexArenaBuffer;
    private DeviceMemory _vertexArenaMemory;
    private nint _vertexArenaMapped;
    private Buffer _indexArenaBuffer;
    private DeviceMemory _indexArenaMemory;
    private nint _indexArenaMapped;

    private Image _tiles;
    private DeviceMemory _tilesMemory;
    private ImageView _tilesView;
    private Sampler _tileSampler;

    private int _shadowCascadeCount;
    private int _shadowResolution;
    private Image _shadowImage;
    private DeviceMemory _shadowMemory;
    private ImageView _shadowArrayView;
    private Sampler _shadowSampler;
    private RenderPass _shadowPass;

    private DescriptorSetLayout _worldSetLayout;
    private DescriptorPool _worldDescriptorPool;
    private PipelineLayout _terrainLayout;
    private Pipeline _terrainPipeline;
    private PipelineLayout _shadowLayout;
    private Pipeline _shadowPipeline;
    private PipelineLayout _bodyLayout;
    private Pipeline _bodyPipeline;

    private Buffer _bodyVertexBuffer;
    private DeviceMemory _bodyVertexMemory;
    private nint _bodyVertexMapped;
    private Buffer _bodyIndexBuffer;
    private DeviceMemory _bodyIndexMemory;
    private nint _bodyIndexMapped;
    private int _bodyIndexCount;
    private long _frameCounter;

    /// <summary>Frames a released chunk's memory waits before reuse: past every frame that could still be reading it.</summary>
    private const int ReleaseDelayFrames = FramesInFlight + 1;

    /// <summary>Chunk sections that could not be uploaded because the arenas were full. Should stay zero.</summary>
    public int ArenaFailures { get; private set; }

    /// <summary>Vertices currently held in the chunk arena.</summary>
    public int ArenaVerticesInUse => _vertexArena.UsedCount;

    /// <summary>Sections drawn in the last frame's main pass, after culling.</summary>
    public int VisibleSectionCount => _visible.Count;

    /// <summary>Player bodies drawn in the last frame.</summary>
    public int VisibleBodyCount { get; private set; }

    /// <summary>
    /// Uploads the one body mesh every remote player is drawn with. Call once, before the first frame that draws bodies.
    /// </summary>
    public void SetBodyMesh(BodyMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (_bodyVertexBuffer.Handle != 0)
        {
            _vk.UnmapMemory(_device, _bodyVertexMemory);
            _vk.UnmapMemory(_device, _bodyIndexMemory);
            _vk.DestroyBuffer(_device, _bodyVertexBuffer, null);
            _vk.FreeMemory(_device, _bodyVertexMemory, null);
            _vk.DestroyBuffer(_device, _bodyIndexBuffer, null);
            _vk.FreeMemory(_device, _bodyIndexMemory, null);
        }

        var vertices = mesh.Vertices.Span;
        var indices = mesh.Indices.Span;
        CreateMappedBuffer((ulong)vertices.Length * (ulong)sizeof(BodyVertex), BufferUsageFlags.VertexBufferBit, out _bodyVertexBuffer, out _bodyVertexMemory, out _bodyVertexMapped);
        CreateMappedBuffer((ulong)indices.Length * sizeof(uint), BufferUsageFlags.IndexBufferBit, out _bodyIndexBuffer, out _bodyIndexMemory, out _bodyIndexMapped);
        vertices.CopyTo(new Span<BodyVertex>((void*)_bodyVertexMapped, vertices.Length));
        indices.CopyTo(new Span<uint>((void*)_bodyIndexMapped, indices.Length));
        _bodyIndexCount = indices.Length;
    }

    private readonly record struct DrawItem(Vector4 Origin, int VertexStart, int IndexStart, int IndexCount);

    private sealed record WorldFrame(WorldScene Scene, ShadowCascade[] Cascades, bool Shadows);

    public GpuChunk? UploadChunk(ChunkMeshSet mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var sections = new List<GpuSection>();
        for (var i = 0; i < mesh.Sections.Count; i++)
        {
            var section = mesh.Sections[i];
            if (section.IsEmpty)
            {
                continue;
            }

            var vertexCount = section.Vertices.Length;
            var indexCount = section.Indices.Length;
            var vertexStart = _vertexArena.Allocate(vertexCount);
            var indexStart = vertexStart < 0 ? -1 : _indexArena.Allocate(indexCount);
            if (vertexStart < 0 || indexStart < 0)
            {
                if (vertexStart >= 0)
                {
                    _vertexArena.Free(vertexStart, vertexCount);
                }

                ArenaFailures++;
                continue;
            }

            fixed (ChunkVertex* source = section.Vertices.Span)
            {
                var target = (byte*)_vertexArenaMapped + ((long)vertexStart * sizeof(ChunkVertex));
                System.Buffer.MemoryCopy(source, target, (long)vertexCount * sizeof(ChunkVertex), (long)vertexCount * sizeof(ChunkVertex));
            }

            fixed (uint* source = section.Indices.Span)
            {
                var target = (byte*)_indexArenaMapped + ((long)indexStart * sizeof(uint));
                System.Buffer.MemoryCopy(source, target, (long)indexCount * sizeof(uint), (long)indexCount * sizeof(uint));
            }

            var low = new Vector3(mesh.Coord.WorldX, i * ChunkConstants.SectionHeight, mesh.Coord.WorldZ);
            sections.Add(new GpuSection(vertexStart, vertexCount, indexStart, indexCount, low, low + new Vector3(ChunkConstants.Size, ChunkConstants.SectionHeight, ChunkConstants.Size)));
        }

        return sections.Count == 0 ? null : new GpuChunk(mesh.Coord, sections);
    }

    public void ReleaseChunk(GpuChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        // Frames already submitted may still be reading this memory, so hand it back a few frames from now.
        _pendingReleases.Enqueue((_frameCounter, chunk));
    }

    private void BeginWorldFrame(WorldScene? scene)
    {
        _ = scene;
        _frameCounter++;
        while (_pendingReleases.Count > 0 && _pendingReleases.Peek().Frame + ReleaseDelayFrames <= _frameCounter)
        {
            var (_, chunk) = _pendingReleases.Dequeue();
            foreach (var section in chunk.Sections)
            {
                _vertexArena.Free(section.VertexStart, section.VertexCount);
                _indexArena.Free(section.IndexStart, section.IndexCount);
            }
        }
    }

    private WorldFrame PrepareWorld(WorldScene scene)
    {
        var camera = scene.Camera;
        var cascadeCount = Math.Min(Math.Min(scene.Shadows.Cascades, _shadowCascadeCount), ShadowSettings.MaxCascades);
        var shadows = cascadeCount > 0 && scene.Sun.CastsShadows;
        var cascades = shadows
            ? CascadeShadows.Compute(camera, scene.Sun.DirectionToSun, cascadeCount, scene.Shadows.Distance, _shadowResolution)
            : [];

        var fogEnd = MathF.Max(32f, (scene.ViewDistanceChunks * ChunkConstants.Size) - 12f);
        var uniforms = new WorldUniforms
        {
            ViewProjection = camera.ViewProjection,
            Cascade0 = cascades.Length > 0 ? cascades[0].ViewProjection : Matrix4x4.Identity,
            Cascade1 = cascades.Length > 1 ? cascades[1].ViewProjection : Matrix4x4.Identity,
            Cascade2 = cascades.Length > 2 ? cascades[2].ViewProjection : Matrix4x4.Identity,
            CascadeSplits = new Vector4(
                cascades.Length > 0 ? cascades[0].SplitFar : 0,
                cascades.Length > 1 ? cascades[1].SplitFar : (cascades.Length > 0 ? cascades[0].SplitFar : 0),
                cascades.Length > 2 ? cascades[2].SplitFar : (cascades.Length > 1 ? cascades[1].SplitFar : (cascades.Length > 0 ? cascades[0].SplitFar : 0)),
                cascades.Length),
            SunDirection = new Vector4(scene.Sun.DirectionToSun, shadows ? 1f : 0f),
            SunColor = new Vector4(scene.Sun.SunColor * scene.Sun.SunIntensity, 0f),
            AmbientColor = new Vector4(scene.Sun.AmbientColor, 0f),
            FogColor = new Vector4(scene.Sun.FogColor, fogEnd * 0.55f),
            CameraPosition = new Vector4(camera.Position, fogEnd),
            ShadowParams = new Vector4(1f / _shadowResolution, 0.0006f, 0f, 0f),
        };
        System.Buffer.MemoryCopy(&uniforms, (void*)_uniformMapped[_frame], sizeof(WorldUniforms), sizeof(WorldUniforms));

        Collect(scene.Chunks, new Frustum(camera.ViewProjection), _visible);
        for (var i = 0; i < _shadowVisible.Length; i++)
        {
            _shadowVisible[i].Clear();
        }

        for (var i = 0; i < cascades.Length; i++)
        {
            Collect(scene.Chunks, new Frustum(cascades[i].ViewProjection), _shadowVisible[i]);
        }

        return new WorldFrame(scene, cascades, shadows);
    }

    private static void Collect(IReadOnlyCollection<GpuChunk> chunks, Frustum frustum, List<DrawItem> into)
    {
        into.Clear();
        foreach (var chunk in chunks)
        {
            var origin = new Vector4(chunk.Origin, 0);
            foreach (var section in chunk.Sections)
            {
                if (frustum.Intersects(section.BoundsMin, section.BoundsMax))
                {
                    into.Add(new DrawItem(origin, section.VertexStart, section.IndexStart, section.IndexCount));
                }
            }
        }
    }

    private void RecordShadowPasses(CommandBuffer cmd, WorldFrame world)
    {
        if (!world.Shadows)
        {
            return;
        }

        var clear = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) };
        var viewport = new Viewport(0, 0, _shadowResolution, _shadowResolution, 0, 1);
        var extent = new Extent2D((uint)_shadowResolution, (uint)_shadowResolution);
        var scissor = new Rect2D(new Offset2D(0, 0), extent);
        var push = stackalloc float[20];

        for (var cascade = 0; cascade < world.Cascades.Length; cascade++)
        {
            var passInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _shadowPass,
                Framebuffer = _shadowFramebuffers[cascade],
                RenderArea = scissor,
                ClearValueCount = 1,
                PClearValues = &clear,
            };
            _vk.CmdBeginRenderPass(cmd, &passInfo, SubpassContents.Inline);
            Mark(cmd, MarkShadowPass + cascade);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _shadowPipeline);
            _vk.CmdSetViewport(cmd, 0, 1, &viewport);
            _vk.CmdSetScissor(cmd, 0, 1, &scissor);
            BindArenas(cmd);

            *(Matrix4x4*)push = world.Cascades[cascade].ViewProjection;
            foreach (var item in _shadowVisible[cascade])
            {
                push[16] = item.Origin.X;
                push[17] = item.Origin.Y;
                push[18] = item.Origin.Z;
                push[19] = 0;
                _vk.CmdPushConstants(cmd, _shadowLayout, ShaderStageFlags.VertexBit, 0, 80, push);
                _vk.CmdDrawIndexed(cmd, (uint)item.IndexCount, 1, (uint)item.IndexStart, item.VertexStart, 0);
            }

            _vk.CmdEndRenderPass(cmd);
        }
    }

    private void RecordTerrain(CommandBuffer cmd, WorldFrame world)
    {
        _ = world;
        Mark(cmd, MarkTerrainStart);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _terrainPipeline);
        var set = _worldSets[_frame];
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _terrainLayout, 0, 1, &set, 0, null);
        BindArenas(cmd);

        var origin = stackalloc float[4];
        var drawn = 0;
        foreach (var item in _visible)
        {
            Mark(cmd, MarkTerrainDraw + drawn++);
            origin[0] = item.Origin.X;
            origin[1] = item.Origin.Y;
            origin[2] = item.Origin.Z;
            origin[3] = 0;
            _vk.CmdPushConstants(cmd, _terrainLayout, ShaderStageFlags.VertexBit, 0, 16, origin);
            _vk.CmdDrawIndexed(cmd, (uint)item.IndexCount, 1, (uint)item.IndexStart, item.VertexStart, 0);
        }
    }

    /// <summary>Draws every remote player as the shared body mesh, one draw per body with its position and yaw pushed.</summary>
    private void RecordBodies(CommandBuffer cmd, WorldFrame world)
    {
        VisibleBodyCount = 0;
        if (_bodyIndexCount == 0 || world.Scene.Bodies.Count == 0)
        {
            return;
        }

        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _bodyPipeline);
        var set = _worldSets[_frame];
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _bodyLayout, 0, 1, &set, 0, null);

        var vertexBuffer = _bodyVertexBuffer;
        ulong offset = 0;
        _vk.CmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
        _vk.CmdBindIndexBuffer(cmd, _bodyIndexBuffer, 0, IndexType.Uint32);

        var push = stackalloc float[4];
        foreach (var body in world.Scene.Bodies)
        {
            push[0] = body.Position.X;
            push[1] = body.Position.Y;
            push[2] = body.Position.Z;
            push[3] = body.Yaw;
            _vk.CmdPushConstants(cmd, _bodyLayout, ShaderStageFlags.VertexBit, 0, 16, push);
            _vk.CmdDrawIndexed(cmd, (uint)_bodyIndexCount, 1, 0, 0, 0);
            VisibleBodyCount++;
        }
    }

    private void BindArenas(CommandBuffer cmd)
    {
        var vertexBuffer = _vertexArenaBuffer;
        ulong offset = 0;
        _vk.CmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
        _vk.CmdBindIndexBuffer(cmd, _indexArenaBuffer, 0, IndexType.Uint32);
    }

    private void CreateDepthBuffer(Extent2D extent)
    {
        CreateImage(extent.Width, extent.Height, 1, DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit, ImageViewType.Type2D, ImageAspectFlags.DepthBit, out _depthImage, out _depthMemory, out _depthView);
    }

    private void DestroyDepthBuffer()
    {
        _vk.DestroyImageView(_device, _depthView, null);
        _vk.DestroyImage(_device, _depthImage, null);
        _vk.FreeMemory(_device, _depthMemory, null);
        _depthView = default;
        _depthImage = default;
        _depthMemory = default;
    }

    private void CreateImage(
        uint width,
        uint height,
        uint layers,
        Format format,
        ImageUsageFlags usage,
        ImageViewType viewType,
        ImageAspectFlags aspect,
        out Image image,
        out DeviceMemory memory,
        out ImageView view)
    {
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = layers,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Image created;
        Check(_vk.CreateImage(_device, &info, null, &created), "creating an image");
        image = created;

        _vk.GetImageMemoryRequirements(_device, image, out var requirements);
        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        DeviceMemory allocated;
        Check(_vk.AllocateMemory(_device, &allocate, null, &allocated), "allocating image memory");
        memory = allocated;
        Check(_vk.BindImageMemory(_device, image, memory, 0), "binding image memory");

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = viewType,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, layers),
        };
        ImageView created2;
        Check(_vk.CreateImageView(_device, &viewInfo, null, &created2), "creating an image view");
        view = created2;
    }

    /// <summary>Creates a persistently mapped buffer in the fastest memory the CPU can write: device-local if the GPU exposes it, otherwise ordinary host memory.</summary>
    private void CreateMappedBuffer(ulong size, BufferUsageFlags usage, out Buffer buffer, out DeviceMemory memory, out nint mapped)
    {
        const MemoryPropertyFlags host = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        try
        {
            CreateBuffer(size, usage, host | MemoryPropertyFlags.DeviceLocalBit, out buffer, out memory);
        }
        catch (InvalidOperationException)
        {
            CreateBuffer(size, usage, host, out buffer, out memory);
        }

        void* pointer;
        Check(_vk.MapMemory(_device, memory, 0, Vk.WholeSize, 0, &pointer), "mapping a buffer");
        mapped = (nint)pointer;
    }

    private void CreateWorldResources()
    {
        _shadowCascadeCount = Math.Clamp(_options.ShadowCascades, 0, ShadowSettings.MaxCascades);
        _shadowResolution = Math.Max(256, _options.ShadowResolution);

        CreateMappedBuffer((ulong)ArenaVertexCapacity * (ulong)sizeof(ChunkVertex), BufferUsageFlags.VertexBufferBit, out _vertexArenaBuffer, out _vertexArenaMemory, out _vertexArenaMapped);
        CreateMappedBuffer((ulong)ArenaIndexCapacity * sizeof(uint), BufferUsageFlags.IndexBufferBit, out _indexArenaBuffer, out _indexArenaMemory, out _indexArenaMapped);
        for (var i = 0; i < FramesInFlight; i++)
        {
            CreateMappedBuffer((ulong)sizeof(WorldUniforms), BufferUsageFlags.UniformBufferBit, out _uniformBuffers[i], out _uniformMemory[i], out _uniformMapped[i]);
        }

        CreateTerrainTiles();
        CreateShadowMaps();
        CreateWorldDescriptors();
        CreateWorldPipelines();
    }

    /// <summary>Uploads the placeholder block textures as one array image, a layer per tile, sampled as sRGB so lighting happens in linear light.</summary>
    private void CreateTerrainTiles()
    {
        var pixels = TerrainTextures.Generate();
        var layers = (uint)TerrainTextures.TileCount;
        var size = (uint)TerrainTextures.TileSize;

        CreateBuffer((ulong)pixels.Length, BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out var staging, out var stagingMemory);
        void* mapped;
        Check(_vk.MapMemory(_device, stagingMemory, 0, Vk.WholeSize, 0, &mapped), "mapping the tile staging buffer");
        pixels.AsSpan().CopyTo(new Span<byte>(mapped, pixels.Length));
        _vk.UnmapMemory(_device, stagingMemory);

        CreateImage(size, size, layers, Format.R8G8B8A8Srgb, ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit, ImageViewType.Type2DArray, ImageAspectFlags.ColorBit, out _tiles, out _tilesMemory, out _tilesView);

        var cmd = BeginOneShot();
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, layers);
        Barrier(cmd, _tiles, range, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, layers),
            ImageExtent = new Extent3D(size, size, 1),
        };
        _vk.CmdCopyBufferToImage(cmd, staging, _tiles, ImageLayout.TransferDstOptimal, 1, &region);
        Barrier(cmd, _tiles, range, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit);
        EndOneShot(cmd);

        _vk.DestroyBuffer(_device, staging, null);
        _vk.FreeMemory(_device, stagingMemory, null);

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
        };
        Sampler sampler;
        Check(_vk.CreateSampler(_device, &samplerInfo, null, &sampler), "creating the tile sampler");
        _tileSampler = sampler;
    }

    /// <summary>One depth image with a layer per cascade, a render pass that writes it, and a comparison sampler that filters 2 by 2 texels in hardware.</summary>
    private void CreateShadowMaps()
    {
        var layers = (uint)Math.Max(1, _shadowCascadeCount);
        var size = (uint)_shadowResolution;
        CreateImage(size, size, layers, DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit, ImageViewType.Type2DArray, ImageAspectFlags.DepthBit, out _shadowImage, out _shadowMemory, out _shadowArrayView);

        // Sampling an image in an undefined layout is invalid, so start in the layout the shader expects.
        var cmd = BeginOneShot();
        Barrier(cmd, _shadowImage, new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, layers), ImageLayout.Undefined, ImageLayout.DepthStencilReadOnlyOptimal, 0, AccessFlags.ShaderReadBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit);
        EndOneShot(cmd);

        var depth = new AttachmentDescription
        {
            Format = DepthFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilReadOnlyOptimal,
        };
        var reference = new AttachmentReference { Attachment = 0, Layout = ImageLayout.DepthStencilAttachmentOptimal };
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            PDepthStencilAttachment = &reference,
        };
        var dependencies = stackalloc SubpassDependency[2];
        dependencies[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.FragmentShaderBit,
            DstStageMask = PipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask = AccessFlags.ShaderReadBit,
            DstAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
        };
        dependencies[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.LateFragmentTestsBit,
            DstStageMask = PipelineStageFlags.FragmentShaderBit,
            SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
        };
        var passInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &depth,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies,
        };
        RenderPass pass;
        Check(_vk.CreateRenderPass(_device, &passInfo, null, &pass), "creating the shadow render pass");
        _shadowPass = pass;

        for (var i = 0; i < _shadowCascadeCount; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _shadowImage,
                ViewType = ImageViewType.Type2D,
                Format = DepthFormat,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, (uint)i, 1),
            };
            ImageView view;
            Check(_vk.CreateImageView(_device, &viewInfo, null, &view), "creating a shadow layer view");
            _shadowLayerViews[i] = view;

            var attachment = view;
            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _shadowPass,
                AttachmentCount = 1,
                PAttachments = &attachment,
                Width = size,
                Height = size,
                Layers = 1,
            };
            Framebuffer framebuffer;
            Check(_vk.CreateFramebuffer(_device, &framebufferInfo, null, &framebuffer), "creating a shadow framebuffer");
            _shadowFramebuffers[i] = framebuffer;
        }

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            CompareEnable = true,
            CompareOp = CompareOp.LessOrEqual,
        };
        Sampler sampler;
        Check(_vk.CreateSampler(_device, &samplerInfo, null, &sampler), "creating the shadow sampler");
        _shadowSampler = sampler;
    }

    private void CreateWorldDescriptors()
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        bindings[0] = new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit };
        bindings[1] = new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit };
        bindings[2] = new DescriptorSetLayoutBinding { Binding = 2, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 3,
            PBindings = bindings,
        };
        DescriptorSetLayout layout;
        Check(_vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, &layout), "creating the world descriptor layout");
        _worldSetLayout = layout;

        var sizes = stackalloc DescriptorPoolSize[2];
        sizes[0] = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = FramesInFlight };
        sizes[1] = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = FramesInFlight * 2 };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2,
            PPoolSizes = sizes,
            MaxSets = FramesInFlight,
        };
        DescriptorPool pool;
        Check(_vk.CreateDescriptorPool(_device, &poolInfo, null, &pool), "creating the world descriptor pool");
        _worldDescriptorPool = pool;

        var layouts = stackalloc DescriptorSetLayout[FramesInFlight];
        for (var i = 0; i < FramesInFlight; i++)
        {
            layouts[i] = _worldSetLayout;
        }

        var allocate = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _worldDescriptorPool,
            DescriptorSetCount = FramesInFlight,
            PSetLayouts = layouts,
        };
        fixed (DescriptorSet* sets = _worldSets)
        {
            Check(_vk.AllocateDescriptorSets(_device, &allocate, sets), "allocating the world descriptor sets");
        }

        var tileInfo = new DescriptorImageInfo { Sampler = _tileSampler, ImageView = _tilesView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var shadowInfo = new DescriptorImageInfo { Sampler = _shadowSampler, ImageView = _shadowArrayView, ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal };
        var writes = stackalloc WriteDescriptorSet[3];
        for (var i = 0; i < FramesInFlight; i++)
        {
            var bufferInfo = new DescriptorBufferInfo { Buffer = _uniformBuffers[i], Offset = 0, Range = (ulong)sizeof(WorldUniforms) };
            writes[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _worldSets[i], DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &bufferInfo };
            writes[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _worldSets[i], DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &tileInfo };
            writes[2] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _worldSets[i], DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &shadowInfo };
            _vk.UpdateDescriptorSets(_device, 3, writes, 0, null);
        }
    }

    private void CreateWorldPipelines()
    {
        var terrainVertex = CreateShader("terrain.vert.spv");
        var terrainFragment = CreateShader("terrain.frag.spv");
        var shadowVertex = CreateShader("shadow.vert.spv");
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var binding = new VertexInputBindingDescription { Binding = 0, Stride = (uint)sizeof(ChunkVertex), InputRate = VertexInputRate.Vertex };
            var attributes = stackalloc VertexInputAttributeDescription[4];
            attributes[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R8G8B8A8Uint, Offset = 0 };
            attributes[1] = new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R16Uint, Offset = 4 };
            attributes[2] = new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = Format.R8G8Uint, Offset = 6 };
            attributes[3] = new VertexInputAttributeDescription { Location = 3, Binding = 0, Format = Format.R8G8B8A8Uint, Offset = 8 };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
            var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
            var depthTest = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = true,
                DepthCompareOp = CompareOp.Less,
            };
            var dynamicStates = stackalloc DynamicState[2];
            dynamicStates[0] = DynamicState.Viewport;
            dynamicStates[1] = DynamicState.Scissor;
            var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };

            // Terrain: all four vertex attributes, back faces culled, written opaque.
            var terrainInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 4,
                PVertexAttributeDescriptions = attributes,
            };
            var terrainRaster = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.BackBit,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1,
            };
            var opaque = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            var terrainBlend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &opaque };

            var terrainStages = stackalloc PipelineShaderStageCreateInfo[2];
            terrainStages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = terrainVertex, PName = entry };
            terrainStages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = terrainFragment, PName = entry };

            var setLayout = _worldSetLayout;
            var terrainPush = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit, Offset = 0, Size = 16 };
            var terrainLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &setLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &terrainPush,
            };
            PipelineLayout terrainLayout;
            Check(_vk.CreatePipelineLayout(_device, &terrainLayoutInfo, null, &terrainLayout), "creating the terrain pipeline layout");
            _terrainLayout = terrainLayout;

            var terrainInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = terrainStages,
                PVertexInputState = &terrainInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &terrainRaster,
                PMultisampleState = &multisample,
                PDepthStencilState = &depthTest,
                PColorBlendState = &terrainBlend,
                PDynamicState = &dynamic,
                Layout = _terrainLayout,
                RenderPass = _renderPass,
                Subpass = 0,
            };
            Pipeline terrain;
            Check(_vk.CreateGraphicsPipelines(_device, default, 1, &terrainInfo, null, &terrain), "creating the terrain pipeline");
            _terrainPipeline = terrain;

            // Shadow: positions only, depth bias to fight acne, no colour output.
            var shadowInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 1,
                PVertexAttributeDescriptions = attributes,
            };
            var shadowRaster = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                DepthBiasEnable = true,
                DepthBiasConstantFactor = 1.25f,
                DepthBiasSlopeFactor = 1.75f,
                LineWidth = 1,
            };
            var shadowBlend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo };
            var shadowStage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = shadowVertex, PName = entry };
            var shadowPush = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit, Offset = 0, Size = 80 };
            var shadowLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &shadowPush,
            };
            PipelineLayout shadowLayout;
            Check(_vk.CreatePipelineLayout(_device, &shadowLayoutInfo, null, &shadowLayout), "creating the shadow pipeline layout");
            _shadowLayout = shadowLayout;

            var shadowInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 1,
                PStages = &shadowStage,
                PVertexInputState = &shadowInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &shadowRaster,
                PMultisampleState = &multisample,
                PDepthStencilState = &depthTest,
                PColorBlendState = &shadowBlend,
                PDynamicState = &dynamic,
                Layout = _shadowLayout,
                RenderPass = _shadowPass,
                Subpass = 0,
            };
            Pipeline shadow;
            Check(_vk.CreateGraphicsPipelines(_device, default, 1, &shadowInfo, null, &shadow), "creating the shadow pipeline");
            _shadowPipeline = shadow;

            // Body: position, normal, and colour, lit like the terrain and drawn after it.
            var bodyVertex = CreateShader("body.vert.spv");
            var bodyFragment = CreateShader("body.frag.spv");
            try
            {
                var bodyBinding = new VertexInputBindingDescription { Binding = 0, Stride = (uint)sizeof(BodyVertex), InputRate = VertexInputRate.Vertex };
                var bodyAttributes = stackalloc VertexInputAttributeDescription[3];
                bodyAttributes[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 };
                bodyAttributes[1] = new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 };
                bodyAttributes[2] = new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = Format.R8G8B8A8Uint, Offset = 24 };

                var bodyInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = 1,
                    PVertexBindingDescriptions = &bodyBinding,
                    VertexAttributeDescriptionCount = 3,
                    PVertexAttributeDescriptions = bodyAttributes,
                };
                var bodyRaster = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.BackBit,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1,
                };
                var bodyStages = stackalloc PipelineShaderStageCreateInfo[2];
                bodyStages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = bodyVertex, PName = entry };
                bodyStages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = bodyFragment, PName = entry };

                var bodyPush = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit, Offset = 0, Size = 16 };
                var bodyLayoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = 1,
                    PSetLayouts = &setLayout,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &bodyPush,
                };
                PipelineLayout bodyLayout;
                Check(_vk.CreatePipelineLayout(_device, &bodyLayoutInfo, null, &bodyLayout), "creating the body pipeline layout");
                _bodyLayout = bodyLayout;

                var bodyInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = bodyStages,
                    PVertexInputState = &bodyInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &bodyRaster,
                    PMultisampleState = &multisample,
                    PDepthStencilState = &depthTest,
                    PColorBlendState = &terrainBlend,
                    PDynamicState = &dynamic,
                    Layout = _bodyLayout,
                    RenderPass = _renderPass,
                    Subpass = 0,
                };
                Pipeline body;
                Check(_vk.CreateGraphicsPipelines(_device, default, 1, &bodyInfo, null, &body), "creating the body pipeline");
                _bodyPipeline = body;
            }
            finally
            {
                _vk.DestroyShaderModule(_device, bodyVertex, null);
                _vk.DestroyShaderModule(_device, bodyFragment, null);
            }
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
            _vk.DestroyShaderModule(_device, terrainVertex, null);
            _vk.DestroyShaderModule(_device, terrainFragment, null);
            _vk.DestroyShaderModule(_device, shadowVertex, null);
        }
    }

    private void DisposeWorld()
    {
        _vk.DestroyPipeline(_device, _terrainPipeline, null);
        _vk.DestroyPipelineLayout(_device, _terrainLayout, null);
        _vk.DestroyPipeline(_device, _shadowPipeline, null);
        _vk.DestroyPipelineLayout(_device, _shadowLayout, null);
        _vk.DestroyPipeline(_device, _bodyPipeline, null);
        _vk.DestroyPipelineLayout(_device, _bodyLayout, null);
        _vk.DestroyDescriptorPool(_device, _worldDescriptorPool, null);
        _vk.DestroyDescriptorSetLayout(_device, _worldSetLayout, null);

        if (_bodyVertexBuffer.Handle != 0)
        {
            _vk.UnmapMemory(_device, _bodyVertexMemory);
            _vk.UnmapMemory(_device, _bodyIndexMemory);
            _vk.DestroyBuffer(_device, _bodyVertexBuffer, null);
            _vk.FreeMemory(_device, _bodyVertexMemory, null);
            _vk.DestroyBuffer(_device, _bodyIndexBuffer, null);
            _vk.FreeMemory(_device, _bodyIndexMemory, null);
        }

        foreach (var framebuffer in _shadowFramebuffers)
        {
            _vk.DestroyFramebuffer(_device, framebuffer, null);
        }

        foreach (var view in _shadowLayerViews)
        {
            _vk.DestroyImageView(_device, view, null);
        }

        _vk.DestroyRenderPass(_device, _shadowPass, null);
        _vk.DestroySampler(_device, _shadowSampler, null);
        _vk.DestroyImageView(_device, _shadowArrayView, null);
        _vk.DestroyImage(_device, _shadowImage, null);
        _vk.FreeMemory(_device, _shadowMemory, null);

        _vk.DestroySampler(_device, _tileSampler, null);
        _vk.DestroyImageView(_device, _tilesView, null);
        _vk.DestroyImage(_device, _tiles, null);
        _vk.FreeMemory(_device, _tilesMemory, null);

        for (var i = 0; i < FramesInFlight; i++)
        {
            if (_uniformMapped[i] != 0)
            {
                _vk.UnmapMemory(_device, _uniformMemory[i]);
            }

            _vk.DestroyBuffer(_device, _uniformBuffers[i], null);
            _vk.FreeMemory(_device, _uniformMemory[i], null);
        }

        if (_vertexArenaMapped != 0)
        {
            _vk.UnmapMemory(_device, _vertexArenaMemory);
        }

        if (_indexArenaMapped != 0)
        {
            _vk.UnmapMemory(_device, _indexArenaMemory);
        }

        _vk.DestroyBuffer(_device, _vertexArenaBuffer, null);
        _vk.FreeMemory(_device, _vertexArenaMemory, null);
        _vk.DestroyBuffer(_device, _indexArenaBuffer, null);
        _vk.FreeMemory(_device, _indexArenaMemory, null);
    }
}
