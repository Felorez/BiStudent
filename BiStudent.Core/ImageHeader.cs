namespace BiStudent;

/// Читает размеры JPEG/PNG из заголовка без декодирования, чтобы отклонять огромные кадры до выделения памяти.
public static class ImageHeader
{
    public const int MaximumSide = 4096;
    public const long MaximumPixels = 12_000_000;

    public static bool TryReadSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length >= 24 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            width = ReadInt32BigEndian(data[16..]);
            height = ReadInt32BigEndian(data[20..]);
            return width > 0 && height > 0;
        }
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;
        var position = 2;
        while (position + 9 < data.Length)
        {
            if (data[position] != 0xFF) { position++; continue; }
            var marker = data[position + 1];
            if (marker == 0xFF) { position++; continue; }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { position += 2; continue; }
            var length = (data[position + 2] << 8) | data[position + 3];
            if (length < 2) return false;
            var isFrame = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isFrame)
            {
                height = (data[position + 5] << 8) | data[position + 6];
                width = (data[position + 7] << 8) | data[position + 8];
                return width > 0 && height > 0;
            }
            position += 2 + length;
        }
        return false;
    }

    /// null — кадр допустим; иначе текст причины отказа.
    public static string? Validate(ReadOnlySpan<byte> data)
    {
        if (!TryReadSize(data, out var width, out var height)) return "Кадр должен быть изображением JPEG или PNG.";
        if (width > MaximumSide || height > MaximumSide || (long)width * height > MaximumPixels)
            return $"Кадр слишком большой ({width}×{height}); максимум {MaximumSide} пикселей по стороне.";
        return null;
    }

    private static int ReadInt32BigEndian(ReadOnlySpan<byte> data) => (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
}
