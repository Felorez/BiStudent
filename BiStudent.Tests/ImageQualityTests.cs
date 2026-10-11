using Xunit;
using OpenCvSharp;

namespace BiStudent.Tests;

public sealed class ImageQualityTests
{
    private static byte[] Encode(string extension, int width, int height)
    {
        using var image = new Mat(new OpenCvSharp.Size(width, height), MatType.CV_8UC3, Scalar.Gray);
        Cv2.ImEncode(extension, image, out var bytes);
        return bytes;
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    public void ReadsSizeFromHeader(string extension)
    {
        Assert.True(ImageHeader.TryReadSize(Encode(extension, 640, 480), out var width, out var height));
        Assert.Equal((640, 480), (width, height));
        Assert.Null(ImageHeader.Validate(Encode(extension, 640, 480)));
    }

    [Fact]
    public void RejectsOversizedImageBeforeDecoding()
    {
        var png = Encode(".png", 16, 16);
        png[16] = 0; png[17] = 0; png[18] = 0x3E; png[19] = 0x80;
        png[20] = 0; png[21] = 0; png[22] = 0x3E; png[23] = 0x80;
        Assert.Contains("слишком большой", ImageHeader.Validate(png));
    }

    [Fact]
    public void RejectsUnknownFormat() =>
        Assert.NotNull(ImageHeader.Validate("not an image"u8.ToArray()));

    [Fact]
    public void AssessFrameReportsMissingFace()
    {
        using var engine = new FaceEngine();
        using var frame = new Mat(new OpenCvSharp.Size(640, 480), MatType.CV_8UC3, Scalar.Black);
        var quality = engine.AssessFrame(frame);
        Assert.False(quality.Ok);
        Assert.Equal("В кадре нет лица", quality.Problem);
        Assert.Throws<InvalidOperationException>(() => engine.CaptureChecked(frame));
    }

    [Fact]
    public void SimilarityOfVectorWithItselfIsOne()
    {
        var vector = new[] { 0.6f, 0.8f };
        Assert.Equal(1, FaceEngine.Similarity(vector, vector), 5);
    }
}
