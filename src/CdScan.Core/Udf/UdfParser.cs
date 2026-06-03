using System.Diagnostics;

namespace CdScan.Core.Udf;

/// <summary>
/// Minimal UDF parser focused on recovering files from damaged AVCHD-style discs.
/// Currently only supports the most common layout (Anchor at 256 → File Set Descriptor → Root Directory).
/// </summary>
public static class UdfParser
{


    /// <summary>
    /// Attempts to walk the UDF filesystem and return all files.
    /// Returns empty list if the disc does not appear to be UDF or if parsing fails.
    /// </summary>
    public static List<Core.FileEntry> GetAllFiles(ISectorReader reader)
    {
        // Try the two most common locations for the Anchor on UDF discs
        AnchorVolumeDescriptorPointer avdp = default;
        bool found = false;

        if (AnchorVolumeDescriptorPointer.TryRead(reader, UdfConstants.AnchorVolumeDescriptorPointerLba, out avdp))
        {
            found = true;
            Console.WriteLine($"[Udf] Found Anchor Volume Descriptor Pointer at LBA {UdfConstants.AnchorVolumeDescriptorPointerLba}");
        }
        else if (AnchorVolumeDescriptorPointer.TryRead(reader, 512, out avdp))
        {
            found = true;
            Console.WriteLine("[Udf] Found Anchor Volume Descriptor Pointer at LBA 512 (secondary location)");
        }

        if (!found)
        {
            Console.WriteLine("[Udf] Could not locate a valid Anchor Volume Descriptor Pointer. This disc may not be UDF or may be too damaged in the anchor area.");
            return new List<Core.FileEntry>();
        }

        Console.WriteLine($"[Udf] Main Volume Descriptor Sequence starts at LBA {avdp.MainVolumeDescriptorSequenceExtentLocation} (length {avdp.MainVolumeDescriptorSequenceExtentLength})");
        Console.WriteLine($"[Udf] Reserve Volume Descriptor Sequence starts at LBA {avdp.ReserveVolumeDescriptorSequenceExtentLocation} (length {avdp.ReserveVolumeDescriptorSequenceExtentLength})");

        // Try Main VDS first
        var fsdLba = VolumeDescriptorSequence.FindFileSetDescriptorLba(
            reader,
            avdp.MainVolumeDescriptorSequenceExtentLocation,
            (uint)avdp.MainVolumeDescriptorSequenceExtentLength);

        if (fsdLba == null && avdp.ReserveVolumeDescriptorSequenceExtentLocation != 0)
        {
            Console.WriteLine("[Udf] Main VDS did not yield FSD. Trying Reserve VDS...");
            fsdLba = VolumeDescriptorSequence.FindFileSetDescriptorLba(
                reader,
                avdp.ReserveVolumeDescriptorSequenceExtentLocation,
                (uint)avdp.ReserveVolumeDescriptorSequenceExtentLength);
        }

        if (fsdLba == null)
        {
            Console.WriteLine("[Udf] Could not locate File Set Descriptor in either VDS copy.");
            Console.WriteLine("[Udf] Falling back to broad scan for File Identifier Descriptors containing .MTS (slow but effective on damaged discs)...");
            Console.WriteLine("[Udf] This can take 10-60+ minutes. Progress will be reported every 100k sectors.");

            var hits = VolumeDescriptorSequence.ScanForFileIdentifierDescriptorsWithSize(
                reader,
                ".MTS",
                startLba: 0,
                maxSectors: 2_000_000,
                progressCallback: (current, total) =>
                {
                    double percent = current * 100.0 / total;
                    Console.WriteLine($"[Udf] Broad scan progress: {current:N0} / {total:N0} sectors ({percent:F1}%)");
                });

            if (hits.Count > 0)
            {
                Console.WriteLine($"\n[Udf] Broad scan found {hits.Count} .MTS entries:");
                foreach (var (lba, name, size, dataLba) in hits)
                {
                    string sizeStr = size > 0 ? $"{size:N0} bytes" : "size unknown";
                    string dataStr = dataLba > 0 ? $", data at {dataLba}" : "";
                    Console.WriteLine($"  {name} at FID LBA {lba} → {sizeStr}{dataStr}");
                }
            }
            else
            {
                Console.WriteLine("[Udf] Broad scan completed but found no .MTS references in File Identifier Descriptors.");
            }

            return new List<Core.FileEntry>();
        }

        Console.WriteLine($"[Udf] File Set Descriptor located at LBA {fsdLba.Value}. Reading it...");

        if (!reader.TryReadSector(fsdLba.Value, out byte[]? fsdSector, out _))
        {
            Console.WriteLine("[Udf] Failed to read File Set Descriptor sector.");
            return new List<Core.FileEntry>();
        }

        var fsdTag = new DescriptorTag(fsdSector.AsSpan(0, 16));
        if (fsdTag.TagIdentifier != UdfConstants.TagIdentifierFileSetDescriptor)
        {
            Console.WriteLine("[Udf] Sector did not contain a valid File Set Descriptor tag.");
            return new List<Core.FileEntry>();
        }

        var fileSet = new FileSetDescriptor(fsdSector);
        Console.WriteLine($"[Udf] Root Directory ICB points to LBA {fileSet.RootDirectoryIcb.ExtentLocation} (Partition {fileSet.RootDirectoryIcb.PartitionReferenceNumber})");

        Console.WriteLine("[Udf] Successfully reached the File Set Descriptor and Root ICB. Directory walking is next.");

        // TODO: Read the Root Directory using the ICB above, then walk File Identifier Descriptors
        // to locate AVCHD/BDMV/STREAM and extract .MTS file extents.

        return new List<Core.FileEntry>();
    }
}
