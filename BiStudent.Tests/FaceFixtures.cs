using OpenCvSharp;

namespace BiStudent.Tests;

internal static class FaceFixtures
{
    public static Mat Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Faces", name + ".jpg");
        var image = Cv2.ImRead(path, ImreadModes.Color);
        if (image.Empty()) throw new InvalidOperationException($"Не удалось прочитать {path}.");
        return image;
    }

    public static Mat Flip(Mat image)
    {
        var result = new Mat();
        Cv2.Flip(image, result, FlipMode.Y);
        return result;
    }

    public static Mat Rotate(Mat image, double degrees)
    {
        using var transform = Cv2.GetRotationMatrix2D(new Point2f(image.Width / 2f, image.Height / 2f), degrees, 1);
        var result = new Mat();
        Cv2.WarpAffine(image, result, transform, new Size(image.Width, image.Height),
            InterpolationFlags.Linear, BorderTypes.Replicate);
        return result;
    }

    public static Mat Brighter(Mat image)
    {
        var result = new Mat();
        image.ConvertTo(result, -1, 1.0, 25);
        return result;
    }

    public static Mat Far(Mat image)
    {
        using var resized = new Mat();
        Cv2.Resize(image, resized, new Size(image.Width * 3 / 4, image.Height * 3 / 4));
        var result = new Mat();
        Cv2.CopyMakeBorder(resized, result, 75, 75, 60, 60, BorderTypes.Replicate);
        return result;
    }

    public static byte[] Jpeg(Mat image)
    {
        Cv2.ImEncode(".jpg", image, out var bytes);
        return bytes;
    }

    public static List<byte[]> EnrollmentSet(string name)
    {
        using var image = Load(name);
        using var flip = Flip(image);
        using var plus = Rotate(image, 15);
        using var minus = Rotate(image, -15);
        return [Jpeg(flip), Jpeg(plus), Jpeg(minus)];
    }

    public static Mat Collage(params Mat[] images)
    {
        var result = new Mat(images[0].Height, images.Sum(image => image.Width), images[0].Type());
        var x = 0;
        foreach (var image in images)
        {
            using var target = new Mat(result, new Rect(x, 0, image.Width, image.Height));
            image.CopyTo(target);
            x += image.Width;
        }
        return result;
    }
}
