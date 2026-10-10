using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace BiStudent;

public sealed record FaceMatch(OpenCvSharp.Rect Bounds, int? StudentId, double Similarity);

public sealed class FaceEngine : IDisposable
{
    private static readonly (double X, double Y)[] AlignedLandmarks =
    [
        (38.2946, 51.6963), (73.5318, 51.5014), (56.0252, 71.7366),
        (41.5493, 92.3655), (70.7299, 92.2041)
    ];

    private readonly string detectionModel;
    private readonly Net recognitionNet;
    private FaceDetectorYN? detector;
    private OpenCvSharp.Size detectorSize;
    private readonly List<(int StudentId, float[] Vector)> enrolled = new();

    // OpenCV Zoo uses 0.363 for cosine matching. A slightly higher threshold
    // and a margin between different students reduce accidental attendance marks.
    public const double MatchThreshold = 0.43;
    public const double MinimumMargin = 0.06;

    public FaceEngine()
    {
        var models = Path.Combine(AppContext.BaseDirectory, "Models");
        detectionModel = Path.Combine(models, "face_detection_yunet_2023mar.onnx");
        var recognitionModel = Path.Combine(models, "face_recognition_sface_2021dec.onnx");
        if (!File.Exists(detectionModel)) throw new FileNotFoundException("Не найдена модель YuNet", detectionModel);
        if (!File.Exists(recognitionModel)) throw new FileNotFoundException("Не найдена модель SFace", recognitionModel);
        recognitionNet = CvDnn.ReadNetFromOnnx(recognitionModel) ?? throw new InvalidOperationException("Не удалось загрузить модель SFace.");
    }

    public void Train(IEnumerable<(int StudentId, byte[] Image)> samples)
    {
        enrolled.Clear();
        foreach (var sample in samples)
        {
            using var aligned = Cv2.ImDecode(sample.Image, ImreadModes.Color);
            if (aligned.Empty() || aligned.Width != 112 || aligned.Height != 112) continue;
            enrolled.Add((sample.StudentId, Feature(aligned)));
        }
    }

    public IReadOnlyList<FaceMatch> Scan(Mat frame)
    {
        using var faces = FindFaces(frame);
        var matches = new List<FaceMatch>();
        var rows = faces.Rows;
        for (var row = 0; row < rows; row++)
        {
            var bounds = Bounds(faces, row, frame.Width, frame.Height);
            if (bounds.Width < 65 || bounds.Height < 65) continue;
            int? id = null;
            double best = 0;
            if (enrolled.Count > 0)
            {
                using var aligned = Align(frame, faces, row);
                var vector = Feature(aligned);
                var scores = new Dictionary<int, double>();
                foreach (var reference in enrolled)
                {
                    var score = Dot(vector, reference.Vector);
                    if (!scores.TryGetValue(reference.StudentId, out var previous) || score > previous)
                        scores[reference.StudentId] = score;
                }
                var ranked = scores.OrderByDescending(pair => pair.Value).Take(2).ToArray();
                if (ranked.Length > 0)
                {
                    best = ranked[0].Value;
                    if (best >= MatchThreshold && (ranked.Length == 1 || best - ranked[1].Value >= MinimumMargin))
                        id = ranked[0].Key;
                }
            }
            matches.Add(new FaceMatch(bounds, id, best));
        }
        return matches;
    }

    public IReadOnlyList<OpenCvSharp.Rect> Detect(Mat frame)
    {
        using var faces = FindFaces(frame);
        var result = new List<OpenCvSharp.Rect>();
        var rows = faces.Rows;
        for (var row = 0; row < rows; row++)
        {
            var bounds = Bounds(faces, row, frame.Width, frame.Height);
            if (bounds.Width >= 80 && bounds.Height >= 80) result.Add(bounds);
        }
        return result;
    }

    public byte[] CaptureSample(Mat frame, int faceIndex = 0, bool requireSingle = false)
    {
        using var faces = FindFaces(frame);
        if (faces.Rows <= faceIndex || (requireSingle && faces.Rows != 1))
            throw new InvalidOperationException("Для образца нужно одно лицо в кадре.");
        using var aligned = Align(frame, faces, faceIndex);
        Cv2.ImEncode(".png", aligned, out var bytes);
        return bytes;
    }

    private Mat FindFaces(Mat frame)
    {
        var size = new OpenCvSharp.Size(frame.Width, frame.Height);
        if (detector is null || detectorSize != size)
        {
            detector?.Dispose();
            detector = FaceDetectorYN.Create(detectionModel, "", size, 0.85f);
            detectorSize = size;
        }
        var faces = new Mat();
        detector.Detect(frame, faces);
        return faces;
    }

    private static OpenCvSharp.Rect Bounds(Mat faces, int row, int width, int height)
    {
        var x = Math.Clamp((int)faces.At<float>(row, 0), 0, width);
        var y = Math.Clamp((int)faces.At<float>(row, 1), 0, height);
        var w = Math.Clamp((int)faces.At<float>(row, 2), 0, width - x);
        var h = Math.Clamp((int)faces.At<float>(row, 3), 0, height - y);
        return new OpenCvSharp.Rect(x, y, w, h);
    }

    private static Mat Align(Mat frame, Mat faces, int row)
    {
        var src = new (double X, double Y)[5];
        for (var i = 0; i < 5; i++)
            src[i] = (faces.At<float>(row, 4 + i * 2), faces.At<float>(row, 5 + i * 2));

        var srcX = src.Average(p => p.X);
        var srcY = src.Average(p => p.Y);
        var dstX = AlignedLandmarks.Average(p => p.X);
        var dstY = AlignedLandmarks.Average(p => p.Y);
        double numeratorA = 0, numeratorB = 0, denominator = 0;
        for (var i = 0; i < 5; i++)
        {
            var x = src[i].X - srcX;
            var y = src[i].Y - srcY;
            var u = AlignedLandmarks[i].X - dstX;
            var v = AlignedLandmarks[i].Y - dstY;
            numeratorA += x * u + y * v;
            numeratorB += x * v - y * u;
            denominator += x * x + y * y;
        }
        if (denominator < 1) throw new InvalidOperationException("Не удалось выровнять лицо.");
        var a = numeratorA / denominator;
        var b = numeratorB / denominator;
        using var transform = new Mat(2, 3, MatType.CV_64FC1);
        transform.Set(0, 0, a); transform.Set(0, 1, -b); transform.Set(0, 2, dstX - a * srcX + b * srcY);
        transform.Set(1, 0, b); transform.Set(1, 1, a); transform.Set(1, 2, dstY - b * srcX - a * srcY);
        var aligned = new Mat();
        Cv2.WarpAffine(frame, aligned, transform, new OpenCvSharp.Size(112, 112));
        return aligned;
    }

    private float[] Feature(Mat aligned)
    {
        using var blob = CvDnn.BlobFromImage(aligned, 1, new OpenCvSharp.Size(112, 112), Scalar.All(0), true, false);
        recognitionNet.SetInput(blob);
        using var output = recognitionNet.Forward();
        output.GetArray<float>(out var vector);
        var length = Math.Sqrt(vector.Sum(value => (double)value * value));
        if (length <= 0) throw new InvalidOperationException("Модель не вернула признаки лица.");
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / length);
        return vector;
    }

    private static double Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double result = 0;
        for (var i = 0; i < a.Length; i++) result += a[i] * b[i];
        return result;
    }

    public void Dispose()
    {
        detector?.Dispose();
        recognitionNet.Dispose();
    }
}
