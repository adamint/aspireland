using System.Buffers.Binary;

namespace MetroOps.AiHost;

internal static class AudioDurationInspector
{
    public static double? GetSeconds(ReadOnlySpan<byte> data, string contentType)
    {
        var mediaType = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return mediaType switch
        {
            "audio/mpeg" or "audio/mp3" => ReadMp3(data),
            "audio/wav" or "audio/x-wav" => ReadWave(data),
            "audio/ogg" or "application/ogg" => ReadOggOpus(data),
            "audio/webm" or "video/webm" => ReadWebM(data),
            "audio/mp4" or "audio/x-m4a" or "video/mp4" => ReadMp4(data),
            _ => null
        };
    }

    private static double? ReadWave(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return null;
        }

        uint byteRate = 0;
        uint dataLength = 0;
        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            var payload = offset + 8;
            if (size > int.MaxValue || payload + (long)size > data.Length) break;
            if (data.Slice(offset, 4).SequenceEqual("fmt "u8) && size >= 12)
            {
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(payload + 8, 4));
            }
            else if (data.Slice(offset, 4).SequenceEqual("data"u8))
            {
                dataLength = size;
            }
            offset = payload + (int)size + ((int)size & 1);
        }
        return byteRate > 0 && dataLength > 0 ? (double)dataLength / byteRate : null;
    }

    private static double? ReadMp3(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        if (data.Length >= 10 && data[..3].SequenceEqual("ID3"u8))
        {
            var tagSize = (data[6] & 0x7f) << 21 | (data[7] & 0x7f) << 14 |
                          (data[8] & 0x7f) << 7 | data[9] & 0x7f;
            offset = 10 + tagSize + ((data[5] & 0x10) != 0 ? 10 : 0);
        }

        double seconds = 0;
        var frames = 0;
        while (offset + 4 <= data.Length)
        {
            var header = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            if ((header & 0xffe00000) != 0xffe00000)
            {
                if (frames > 0) break;
                offset++;
                continue;
            }

            var versionBits = (int)(header >> 19) & 3;
            var layerBits = (int)(header >> 17) & 3;
            var bitrateIndex = (int)(header >> 12) & 15;
            var sampleIndex = (int)(header >> 10) & 3;
            if (versionBits == 1 || layerBits == 0 || bitrateIndex is 0 or 15 || sampleIndex == 3)
            {
                if (frames > 0) break;
                offset++;
                continue;
            }

            var version = versionBits == 3 ? 1 : versionBits == 2 ? 2 : 25;
            var layer = 4 - layerBits;
            var bitrate = Bitrate(version, layer, bitrateIndex);
            var sampleRate = new[] { 44100, 48000, 32000 }[sampleIndex] /
                             (version == 1 ? 1 : version == 2 ? 2 : 4);
            var padding = (int)(header >> 9) & 1;
            var frameLength = layer == 1
                ? (12 * bitrate * 1000 / sampleRate + padding) * 4
                : (layer == 3 && version != 1 ? 72 : 144) * bitrate * 1000 / sampleRate + padding;
            if (frameLength <= 4 || offset + frameLength > data.Length) break;

            var samples = layer == 1 ? 384 : layer == 2 || version == 1 ? 1152 : 576;
            seconds += (double)samples / sampleRate;
            frames++;
            offset += frameLength;
        }
        return frames > 0 ? seconds : null;
    }

    private static int Bitrate(int version, int layer, int index)
    {
        int[] table = version == 1
            ? layer switch
            {
                1 => [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448],
                2 => [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],
                _ => [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320]
            }
            : layer == 1
                ? [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256]
                : [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
        return table[index];
    }

    private static double? ReadOggOpus(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        long maximumGranule = -1;
        ushort preSkip = 0;
        var opus = false;
        while (offset + 27 <= data.Length)
        {
            if (!data.Slice(offset, 4).SequenceEqual("OggS"u8)) return null;
            var segments = data[offset + 26];
            if (offset + 27 + segments > data.Length) return null;
            var bodyLength = 0;
            for (var i = 0; i < segments; i++) bodyLength += data[offset + 27 + i];
            var body = offset + 27 + segments;
            if (body + bodyLength > data.Length) return null;
            if (!opus && bodyLength >= 12 && data.Slice(body, 8).SequenceEqual("OpusHead"u8))
            {
                opus = true;
                preSkip = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(body + 10, 2));
            }
            var granule = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset + 6, 8));
            if (granule > maximumGranule) maximumGranule = granule;
            offset = body + bodyLength;
        }
        return opus && maximumGranule >= preSkip ? (double)(maximumGranule - preSkip) / 48000 : null;
    }

    private static double? ReadWebM(ReadOnlySpan<byte> data)
    {
        long timecodeScale = 1_000_000;
        var scaleIndex = IndexOf(data, [0x2a, 0xd7, 0xb1]);
        if (scaleIndex >= 0 && TryReadEbmlValue(data, scaleIndex + 3, out var scaleBytes, out var scaleOffset))
        {
            timecodeScale = (long)ReadUnsigned(data.Slice(scaleOffset, scaleBytes));
        }

        var durationIndex = IndexOf(data, [0x44, 0x89]);
        if (durationIndex >= 0 && TryReadEbmlValue(data, durationIndex + 2, out var durationBytes, out var durationOffset))
        {
            var duration = durationBytes switch
            {
                4 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(data.Slice(durationOffset, 4))),
                8 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(data.Slice(durationOffset, 8))),
                _ => double.NaN
            };
            if (double.IsFinite(duration) && duration > 0)
            {
                return duration * timecodeScale / 1_000_000_000d;
            }
        }

        return ReadWebMClusters(data, timecodeScale);
    }

    private static double? ReadWebMClusters(ReadOnlySpan<byte> data, long timecodeScale)
    {
        ReadOnlySpan<byte> clusterId = [0x1f, 0x43, 0xb6, 0x75];
        var search = 0;
        double maximumUnits = -1;
        while (search < data.Length)
        {
            var relative = IndexOf(data[search..], clusterId);
            if (relative < 0) break;
            var cluster = search + relative;
            var nextRelative = IndexOf(data[(cluster + clusterId.Length)..], clusterId);
            var end = nextRelative < 0 ? data.Length : cluster + clusterId.Length + nextRelative;
            long clusterTime = 0;
            var timeIndex = IndexOf(data.Slice(cluster, end - cluster), [0xe7]);
            if (timeIndex >= 0 &&
                TryReadEbmlValue(data, cluster + timeIndex + 1, out var timeBytes, out var timeOffset))
            {
                clusterTime = (long)ReadUnsigned(data.Slice(timeOffset, timeBytes));
            }

            var cursor = cluster;
            while (cursor < end)
            {
                var blockRelative = IndexOf(data.Slice(cursor, end - cursor), [0xa3]);
                if (blockRelative < 0) break;
                var block = cursor + blockRelative;
                if (!TryReadEbmlValue(data, block + 1, out var blockBytes, out var payload) ||
                    blockBytes < 4 || payload + blockBytes > end)
                {
                    cursor = block + 1;
                    continue;
                }
                if (!TryReadVInt(data, payload, out _, out var trackBytes) || payload + trackBytes + 3 > end)
                {
                    cursor = payload + blockBytes;
                    continue;
                }
                var relativeTime = BinaryPrimitives.ReadInt16BigEndian(data.Slice(payload + trackBytes, 2));
                var packet = payload + trackBytes + 3;
                var packetLength = blockBytes - trackBytes - 3;
                var packetMilliseconds = packetLength > 0 ? OpusPacketMilliseconds(data.Slice(packet, packetLength)) : 120;
                var packetUnits = packetMilliseconds * 1_000_000d / timecodeScale;
                maximumUnits = Math.Max(maximumUnits, clusterTime + relativeTime + packetUnits);
                cursor = payload + blockBytes;
            }
            search = end;
        }
        return maximumUnits >= 0 ? maximumUnits * timecodeScale / 1_000_000_000d : null;
    }

    private static double OpusPacketMilliseconds(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty) return 120;
        var toc = packet[0];
        var configuration = toc >> 3;
        var frameMilliseconds = configuration switch
        {
            < 12 => new[] { 10d, 20d, 40d, 60d }[configuration & 3],
            < 16 => new[] { 10d, 20d }[configuration & 1],
            _ => new[] { 2.5d, 5d, 10d, 20d }[configuration & 3]
        };
        var count = (toc & 3) switch
        {
            0 => 1,
            1 or 2 => 2,
            _ when packet.Length > 1 => packet[1] & 0x3f,
            _ => 1
        };
        return Math.Min(120, frameMilliseconds * count);
    }

    private static double? ReadMp4(ReadOnlySpan<byte> data) => ReadMp4Atoms(data, 0, data.Length);

    private static double? ReadMp4Atoms(ReadOnlySpan<byte> data, int start, int end)
    {
        var offset = start;
        while (offset + 8 <= end)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            var header = 8;
            long atomSize = size;
            if (size == 1)
            {
                if (offset + 16 > end) return null;
                atomSize = (long)BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset + 8, 8));
                header = 16;
            }
            else if (size == 0)
            {
                atomSize = end - offset;
            }
            if (atomSize < header || offset + atomSize > end) return null;

            var type = data.Slice(offset + 4, 4);
            if (type.SequenceEqual("mvhd"u8))
            {
                var payload = offset + header;
                if (payload + 20 > offset + atomSize) return null;
                var version = data[payload];
                var timescaleOffset = payload + (version == 1 ? 20 : 12);
                var durationOffset = timescaleOffset + 4;
                if (durationOffset + (version == 1 ? 8 : 4) > offset + atomSize) return null;
                var timescale = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(timescaleOffset, 4));
                var duration = version == 1
                    ? BinaryPrimitives.ReadUInt64BigEndian(data.Slice(durationOffset, 8))
                    : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(durationOffset, 4));
                return timescale > 0 ? (double)duration / timescale : null;
            }
            if (type.SequenceEqual("moov"u8))
            {
                var nested = ReadMp4Atoms(data, offset + header, offset + (int)atomSize);
                if (nested is not null) return nested;
            }
            offset += (int)atomSize;
        }
        return null;
    }

    private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value) => data.IndexOf(value);

    private static bool TryReadEbmlValue(
        ReadOnlySpan<byte> data,
        int sizeOffset,
        out int valueBytes,
        out int valueOffset)
    {
        valueBytes = 0;
        valueOffset = 0;
        if (!TryReadVInt(data, sizeOffset, out var size, out var sizeBytes) ||
            size > int.MaxValue ||
            sizeOffset + sizeBytes + (long)size > data.Length)
        {
            return false;
        }
        valueBytes = (int)size;
        valueOffset = sizeOffset + sizeBytes;
        return true;
    }

    private static bool TryReadVInt(ReadOnlySpan<byte> data, int offset, out ulong value, out int bytes)
    {
        value = 0;
        bytes = 0;
        if ((uint)offset >= (uint)data.Length || data[offset] == 0) return false;
        var mask = 0x80;
        bytes = 1;
        while ((data[offset] & mask) == 0)
        {
            mask >>= 1;
            bytes++;
            if (bytes > 8) return false;
        }
        if (offset + bytes > data.Length) return false;
        value = (ulong)(data[offset] & (mask - 1));
        for (var i = 1; i < bytes; i++) value = value << 8 | data[offset + i];
        return true;
    }

    private static ulong ReadUnsigned(ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        foreach (var item in data) value = value << 8 | item;
        return value;
    }
}
