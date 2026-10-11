using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BiStudent
{
    internal static class Program
    {
        /// <summary>
        /// Главная точка входа для приложения.
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Contains("--self-test"))
            {
                try
                {
                    var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics");
                    var store = new AttendanceStore(directory);
                    using var engine = new FaceEngine();
                    using var frame = new OpenCvSharp.Mat(new OpenCvSharp.Size(640, 480), OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.Black);
                    engine.Scan(frame);
                    if (args.Length > 1)
                    {
                        using var photo = OpenCvSharp.Cv2.ImRead(args[1]);
                        var faces = engine.Detect(photo);
                        var count = faces.Count;
                        File.WriteAllText(Path.Combine(directory, "faces-found.txt"), count.ToString());
                        if (count > 0)
                        {
                            var sample = engine.CaptureSample(photo);
                            engine.Train([(123, sample), (123, sample), (123, sample)]);
                            var found = engine.Scan(photo);
                            File.WriteAllLines(Path.Combine(directory, "recognition-scores.txt"), found.Select(m => $"{m.Bounds.X},{m.Bounds.Y}: id={m.StudentId?.ToString() ?? "unknown"}, similarity={m.Similarity:F3}"));
                            if (!found.Any(m => m.StudentId == 123)) throw new InvalidOperationException("Распознавание не подтвердило зарегистрированное лицо.");
                            foreach (var angle in new[] { -12, 12 })
                            {
                                using var transform = OpenCvSharp.Cv2.GetRotationMatrix2D(new OpenCvSharp.Point2f(photo.Width / 2f, photo.Height / 2f), angle, 1);
                                using var rotated = new OpenCvSharp.Mat();
                                OpenCvSharp.Cv2.WarpAffine(photo, rotated, transform, photo.Size());
                                var rotatedMatches = engine.Scan(rotated);
                                File.WriteAllLines(Path.Combine(directory, $"rotated-{angle}.txt"), rotatedMatches.Select(m => $"id={m.StudentId?.ToString() ?? "unknown"}, similarity={m.Similarity:F3}"));
                                if (!rotatedMatches.Any(m => m.StudentId == 123)) throw new InvalidOperationException($"Лицо не распознано при повороте изображения на {angle}°.");
                            }
                        }
                    }
                    var dataTest = new AttendanceStore(Path.Combine(directory, "store-test"));
                    using var sampleImage = new OpenCvSharp.Mat(new OpenCvSharp.Size(112, 112), OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.Black);
                    OpenCvSharp.Cv2.ImEncode(".png", sampleImage, out var sampleBytes);
                    var testStudent = dataTest.AddStudent("Тестовый студент", "Т-1", [sampleBytes]);
                    if (dataTest.Samples().Last().Image.Length != sampleBytes.Length) throw new InvalidOperationException("Образец не расшифрован.");
                    dataTest.AddLesson("Тест", "Т-1", DateTime.Today.DayOfWeek, TimeSpan.Zero, new TimeSpan(23, 59, 0));
                    var testLesson = dataTest.Lessons().Last();
                    if (!dataTest.MarkPresent(testLesson.Id, DateOnly.FromDateTime(DateTime.Today), testStudent, "Тест")) throw new InvalidOperationException("Отметка не создана.");
                    if (dataTest.MarkPresent(testLesson.Id, DateOnly.FromDateTime(DateTime.Today), testStudent, "Тест")) throw new InvalidOperationException("Создана повторная отметка.");
                    var migrationDirectory = Path.Combine(directory, "migration-test");
                    Directory.CreateDirectory(migrationDirectory);
                    var migrationFile = Path.Combine(migrationDirectory, "attendance.db");
                    if (File.Exists(migrationFile)) File.Delete(migrationFile);
                    using (var oldConnection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={migrationFile}"))
                    {
                        oldConnection.Open();
                        using var oldSchema = oldConnection.CreateCommand();
                        oldSchema.CommandText = "CREATE TABLE students(id INTEGER PRIMARY KEY,name TEXT NOT NULL,group_name TEXT NOT NULL); CREATE TABLE face_samples(id INTEGER PRIMARY KEY,student_id INTEGER NOT NULL,image BLOB NOT NULL); INSERT INTO students VALUES(1,'Старый студент','Т-2');";
                        oldSchema.ExecuteNonQuery();
                        using var oldSample = oldConnection.CreateCommand();
                        oldSample.CommandText = "INSERT INTO face_samples(student_id,image) VALUES(1,$image)";
                        oldSample.Parameters.AddWithValue("$image", System.Security.Cryptography.ProtectedData.Protect(sampleBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
                        oldSample.ExecuteNonQuery();
                    }
                    var migrated = new AttendanceStore(migrationDirectory);
                    if (migrated.Students().Single().SampleCount != 0) throw new InvalidOperationException("Старые образцы не отделены от новых.");
                    migrated.AddLesson("Тест", "Т-2", DateTime.Today.DayOfWeek, TimeSpan.Zero, new TimeSpan(23, 59, 0));
                    var migrationLesson = migrated.Lessons().Single();
                    migrated.MarkPresent(migrationLesson.Id, DateOnly.FromDateTime(DateTime.Today), 1, "Тест");
                    migrated.ReplaceSamples(1, [sampleBytes]);
                    if (migrated.Students().Single().SampleCount != 1 || migrated.Attendance(migrationLesson.Id, DateOnly.FromDateTime(DateTime.Today), "Т-2").Single().SeenAt is null)
                        throw new InvalidOperationException("Обновление лица изменило историю посещаемости.");
                    Application.EnableVisualStyles();
                    using (var form = new MainForm(store))
                    {
                        form.Show();
                        Application.DoEvents();
                        using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                        bitmap.Save(Path.Combine(directory, "students-screen.png"));
                        typeof(MainForm).GetMethod("ShowCamera", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(form, null);
                        Application.DoEvents();
                        using var cameraBitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(cameraBitmap, new System.Drawing.Rectangle(0, 0, cameraBitmap.Width, cameraBitmap.Height));
                        cameraBitmap.Save(Path.Combine(directory, "camera-screen.png"));
                        form.Close();
                    }
                    File.WriteAllText(Path.Combine(directory, "result.txt"), "OK");
                    return 0;
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test-error.txt"), ex.ToString());
                    return 1;
                }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
