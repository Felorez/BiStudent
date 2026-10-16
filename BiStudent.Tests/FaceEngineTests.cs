using BiStudent;
using OpenCvSharp;
using Xunit;

namespace BiStudent.Tests;

public sealed class FaceEngineTests
{
    [Theory]
    [InlineData("armstrong")]
    [InlineData("aldrin")]
    [InlineData("ride")]
    public void Detect_FindsOneFaceInEachPortrait(string name)
    {
        using var engine = new FaceEngine();
        using var frame = FaceFixtures.Load(name);
        Assert.Single(engine.Detect(frame));
    }

    [Fact]
    public void Detect_FindsTwoFacesAndNone()
    {
        using var engine = new FaceEngine();
        using var armstrong = FaceFixtures.Load("armstrong");
        using var aldrin = FaceFixtures.Load("aldrin");
        using var collage = FaceFixtures.Collage(armstrong, aldrin);
        using var black = new Mat(armstrong.Height, armstrong.Width, armstrong.Type(), Scalar.All(0));
        Assert.Equal(2, engine.Detect(collage).Count);
        Assert.Empty(engine.Detect(black));
    }

    [Fact]
    public void AssessFrame_ReportsQualityProblems()
    {
        using var engine = new FaceEngine();
        using var frame = FaceFixtures.Load("armstrong");
        Assert.True(engine.AssessFrame(frame).Ok);
        using var far = FaceFixtures.Far(frame);
        var farQuality = engine.AssessFrame(far);
        Assert.False(farQuality.Ok);
        Assert.Contains("далеко", farQuality.Problem);
        using var dark = new Mat();
        frame.ConvertTo(dark, -1, 0.15, 0);
        var darkQuality = engine.AssessFrame(dark);
        Assert.False(darkQuality.Ok);
        Assert.False(string.IsNullOrWhiteSpace(darkQuality.Problem));
        using var blurred = new Mat();
        Cv2.GaussianBlur(frame, blurred, new Size(31, 31), 0);
        Assert.False(engine.AssessFrame(blurred).Ok);
    }

    [Fact]
    public void CaptureChecked_ProducesNormalizedSampleAndRejectsBadFrames()
    {
        using var engine = new FaceEngine();
        using var frame = FaceFixtures.Load("armstrong");
        var sample = engine.CaptureChecked(frame);
        Assert.NotEmpty(sample.Image);
        Assert.NotEmpty(sample.Embedding);
        var norm = Math.Sqrt(sample.Embedding.Sum(value => (double)value * value));
        Assert.InRange(norm, 0.99, 1.01);
        using var far = FaceFixtures.Far(frame);
        var problem = engine.AssessFrame(far).Problem;
        var exception = Assert.Throws<InvalidOperationException>(() => engine.CaptureChecked(far));
        Assert.Contains(problem!, exception.Message);
        using var aldrin = FaceFixtures.Load("aldrin");
        using var collage = FaceFixtures.Collage(frame, aldrin);
        Assert.Throws<InvalidOperationException>(() => engine.CaptureChecked(collage));
    }

    [Fact]
    public void CaptureSample_EmbeddingMatchesCaptureChecked()
    {
        using var engine = new FaceEngine();
        using var frame = FaceFixtures.Load("armstrong");
        var sample = engine.CaptureChecked(frame);
        var image = engine.CaptureSample(frame);
        Assert.True(FaceEngine.Similarity(sample.Embedding, engine.Embedding(image)) > 0.99);
    }

    [Fact]
    public void Similarity_SeparatesPeopleAndIsSymmetric()
    {
        using var engine = new FaceEngine();
        using var armstrong = FaceFixtures.Load("armstrong");
        using var flip = FaceFixtures.Flip(armstrong);
        using var aldrin = FaceFixtures.Load("aldrin");
        var first = engine.CaptureChecked(armstrong).Embedding;
        var same = engine.CaptureChecked(flip).Embedding;
        var different = engine.CaptureChecked(aldrin).Embedding;
        Assert.True(FaceEngine.Similarity(first, same) > 0.8);
        Assert.True(FaceEngine.Similarity(first, different) < 0.3);
        Assert.Equal(FaceEngine.Similarity(first, same), FaceEngine.Similarity(same, first), 6);
    }

    [Fact]
    public void TrainAndScan_MatchKnownFacesAndKeepBoundsInsideFrame()
    {
        using var engine = new FaceEngine();
        engine.Train(Crops(engine, (1, "armstrong"), (2, "aldrin")));
        using var armstrong = FaceFixtures.Load("armstrong");
        using var bright = FaceFixtures.Brighter(armstrong);
        var known = Assert.Single(engine.Scan(bright));
        Assert.Equal(1, known.StudentId);
        Assert.True(known.Similarity > engine.MatchThreshold);
        using var ride = FaceFixtures.Load("ride");
        Assert.Null(Assert.Single(engine.Scan(ride)).StudentId);
        using var aldrin = FaceFixtures.Load("aldrin");
        using var collage = FaceFixtures.Collage(armstrong, aldrin);
        var matches = engine.Scan(collage);
        Assert.Equal(2, matches.Count);
        Assert.Equal(new[] { 1, 2 }, matches.Select(match => match.StudentId!.Value).Order().ToArray());
        foreach (var match in matches)
        {
            Assert.True(match.Bounds.X >= 0 && match.Bounds.Y >= 0);
            Assert.True(match.Bounds.Right <= collage.Width && match.Bounds.Bottom <= collage.Height);
        }
    }

    [Fact]
    public void MostSimilar_ExcludesRequestedStudent()
    {
        using var engine = new FaceEngine();
        engine.Train(Crops(engine, (1, "armstrong"), (2, "aldrin")));
        using var armstrong = FaceFixtures.Load("armstrong");
        var vector = engine.CaptureChecked(armstrong).Embedding;
        var other = engine.MostSimilar(vector, 1);
        var own = engine.MostSimilar(vector, 2);
        Assert.NotNull(other);
        Assert.Equal(2, other.Value.StudentId);
        Assert.True(other.Value.Similarity < 0.3);
        Assert.NotNull(own);
        Assert.Equal(1, own.Value.StudentId);
        Assert.True(own.Value.Similarity > 0.8);
    }

    [Fact]
    public void Scan_RespectsThresholdAndMinimumMargin()
    {
        using var engine = new FaceEngine();
        var armstrong = Crops(engine, (1, "armstrong"));
        engine.Train(armstrong);
        using var portrait = FaceFixtures.Load("armstrong");
        using var bright = FaceFixtures.Brighter(portrait);
        engine.MatchThreshold = 0.99;
        Assert.Null(Assert.Single(engine.Scan(bright)).StudentId);
        engine.MatchThreshold = FaceEngine.DefaultMatchThreshold;
        engine.Train(armstrong.Concat(armstrong.Select(sample => (3, sample.Image))));
        var ambiguous = Assert.Single(engine.Scan(bright));
        Assert.Null(ambiguous.StudentId);
        Assert.NotNull(ambiguous.CandidateId);
        Assert.True(ambiguous.Margin < engine.MinimumMargin);
    }

    [Fact]
    public void Dispose_TwiceDoesNotThrow()
    {
        var engine = new FaceEngine();
        engine.Dispose();
        engine.Dispose();
    }

    private static List<(int StudentId, byte[] Image)> Crops(FaceEngine engine, params (int StudentId, string Name)[] people)
    {
        var samples = new List<(int StudentId, byte[] Image)>();
        foreach (var (studentId, name) in people)
            foreach (var jpeg in FaceFixtures.EnrollmentSet(name))
            {
                using var frame = Cv2.ImDecode(jpeg, ImreadModes.Color);
                samples.Add((studentId, engine.CaptureChecked(frame).Image));
            }
        return samples;
    }
}
