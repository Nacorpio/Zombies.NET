using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.NV;

namespace Zombies.Engine.Render.Vulkan;

/// <summary>
/// Opt-in help for finding out why a GPU died. With <c>ZOMBIES_VK_DIAG=1</c> the renderer drops numbered checkpoints into each command
/// buffer and, if the device is lost, reads back the last checkpoint the GPU reached and the driver's own fault report.
/// Needs NVIDIA's diagnostic checkpoints and the device-fault extension; without them the switch does nothing.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private const string CheckpointsExtension = "VK_NV_device_diagnostic_checkpoints";
    private const string DeviceFaultExtension = "VK_EXT_device_fault";

    // Checkpoint markers are plain numbers. These name the phases so a report is readable.
    private const int MarkFrameStart = 0x0100;
    private const int MarkShadowPass = 0x0200;
    private const int MarkTerrainStart = 0x0300;
    private const int MarkTerrainDraw = 0x10000;
    private const int MarkSpritesStart = 0x0400;
    private const int MarkFrameEnd = 0x0500;

    private static readonly bool DiagnosticsRequested = Environment.GetEnvironmentVariable("ZOMBIES_VK_DIAG") == "1";

    private NVDeviceDiagnosticCheckpoints? _checkpoints;
    private ExtDeviceFault? _deviceFault;

    private bool SupportsDeviceExtension(PhysicalDevice device, string name)
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
            var extensionName = extension.ExtensionName;
            if (Marshal.PtrToStringAnsi((nint)extensionName) == name)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Records a checkpoint in the command buffer. Does nothing unless diagnostics are on and supported.</summary>
    private void Mark(CommandBuffer cmd, int code) => _checkpoints?.CmdSetCheckpoint(cmd, (void*)(nint)code);

    private static string NameOf(int code) => code switch
    {
        MarkFrameStart => "frame start",
        MarkTerrainStart => "terrain pass start",
        MarkSpritesStart => "sprite pass start",
        MarkFrameEnd => "frame end",
        >= MarkTerrainDraw => $"terrain draw #{code - MarkTerrainDraw}",
        >= MarkShadowPass and < MarkTerrainStart => $"shadow cascade {code - MarkShadowPass}",
        _ => $"marker 0x{code:X}",
    };

    /// <summary>Text describing where the GPU stopped and what the driver says went wrong. Empty when diagnostics are off.</summary>
    private string DescribeDeviceLoss()
    {
        if (_checkpoints is null && _deviceFault is null)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        if (_checkpoints is not null)
        {
            uint count = 0;
            _checkpoints.GetQueueCheckpointData(_queue, &count, null);
            var data = new CheckpointDataNV[count];
            for (var i = 0; i < count; i++)
            {
                data[i].SType = StructureType.CheckpointDataNV;
            }

            fixed (CheckpointDataNV* d = data)
            {
                _checkpoints.GetQueueCheckpointData(_queue, &count, d);
            }

            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{Environment.NewLine}  last checkpoints reached by the GPU ({count}):");
            foreach (var checkpoint in data)
            {
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{Environment.NewLine}    {NameOf((int)(nint)checkpoint.PCheckpointMarker)} (stage {checkpoint.Stage})");
            }
        }

        if (_deviceFault is not null)
        {
            var counts = new DeviceFaultCountsEXT { SType = StructureType.DeviceFaultCountsExt };
            var result = _deviceFault.GetDeviceFaultInfo(_device, &counts, (DeviceFaultInfoEXT*)null);
            if (result == Result.Success)
            {
                var addresses = new DeviceFaultAddressInfoEXT[counts.AddressInfoCount];
                var vendors = new DeviceFaultVendorInfoEXT[counts.VendorInfoCount];
                fixed (DeviceFaultAddressInfoEXT* a = addresses)
                fixed (DeviceFaultVendorInfoEXT* v = vendors)
                {
                    var info = new DeviceFaultInfoEXT
                    {
                        SType = StructureType.DeviceFaultInfoExt,
                        PAddressInfos = a,
                        PVendorInfos = v,
                    };
                    if (_deviceFault.GetDeviceFaultInfo(_device, &counts, &info) == Result.Success)
                    {
                        var description = Marshal.PtrToStringAnsi((nint)info.Description);
                        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{Environment.NewLine}  driver fault report: {description}");
                        foreach (var address in addresses)
                        {
                            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{Environment.NewLine}    {address.AddressType} at 0x{address.ReportedAddress:X} (precision {address.AddressPrecision})");
                        }

                        foreach (var vendor in vendors)
                        {
                            var vendorDescription = Marshal.PtrToStringAnsi((nint)vendor.Description);
                            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{Environment.NewLine}    vendor: {vendorDescription} code 0x{vendor.VendorFaultCode:X} data 0x{vendor.VendorFaultData:X}");
                        }
                    }
                }
            }
        }

        return text.ToString();
    }
}
