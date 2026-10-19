using BiStudent;
using OpenCvSharp;
using Xunit;

namespace BiStudent.Tests;

public sealed class FaceEngineLargeFaceTests
{
    [Theory]
    [InlineData("armstrong")]
    [InlineData("aldrin")]
    [InlineData("ride")]
    public void CaptureChecked_FindsFaceFillingFrame(string name)
    {
        using var engine = new FaceEngine();
        using var frame = FaceFixtures.Load(name);
        var face = Assert.Single(engine.Detect(frame));
        var margin = (int)Math.Round(face.Width * 0.05);
        var left = Math.Max(0, face.X - margin);
        var top = Math.Max(0, face.Y - margin);
        var right = Math.Min(frame.Width, face.Right + margin);
        var bottom = Math.Min(frame.Height, face.Bottom + margin);
        using var cropped = new Mat(frame, new Rect(left, top, right - left, bottom - top));
        var scale = 1280d / Math.Max(cropped.Width, cropped.Height);
        using var big = new Mat();
        Cv2.Resize(cropped, big, new Size((int)Math.Round(cropped.Width * scale),
            (int)Math.Round(cropped.Height * scale)), 0, 0, InterpolationFlags.Cubic);
        Assert.NotEqual("В кадре нет лица", engine.AssessFrame(big).Problem);
        engine.CaptureChecked(big);
    }
}
