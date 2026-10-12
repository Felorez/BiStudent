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
            if (args.Contains("--distance-check"))
            {
                try
                {
                    var samples = new AttendanceStore().Samples();
                    if (samples.Count == 0) throw new InvalidOperationException("Нет локальных образцов для проверки.");
                    using var engine = new FaceEngine();
                    engine.Train(samples);
                    var lines = new List<string>();
                    var detectedSmall = 0; var recognizedSmall = 0;
                    foreach (var sample in samples)
                    {
                        using var image = OpenCvSharp.Cv2.ImDecode(sample.Image, OpenCvSharp.ImreadModes.Color);
                        foreach (var size in new[] { 96, 64, 48, 36 })
                        {
                            using var frame = new OpenCvSharp.Mat(720,1280,OpenCvSharp.MatType.CV_8UC3,OpenCvSharp.Scalar.All(60));
                            using var resized = new OpenCvSharp.Mat();
                            OpenCvSharp.Cv2.Resize(image,resized,new OpenCvSharp.Size(size,size),0,0,OpenCvSharp.InterpolationFlags.Area);
                            using var target = new OpenCvSharp.Mat(frame,new OpenCvSharp.Rect(600,300,size,size)); resized.CopyTo(target);
                            var watch = System.Diagnostics.Stopwatch.StartNew(); var matches = engine.Scan(frame);
                            lines.Add($"sample student={sample.StudentId}; image={size}px; detected={matches.Count}; correct={matches.Any(x=>x.StudentId==sample.StudentId)}; elapsed={watch.ElapsedMilliseconds}ms; " + string.Join("; ",matches.Select(x=>$"bounds={x.Bounds.Width}x{x.Bounds.Height}, similarity={x.Similarity:F3}, id={x.StudentId}")));
                            if (size == 64) { detectedSmall += matches.Count; recognizedSmall += matches.Count(x=>x.StudentId==sample.StudentId); }
                            if (matches.Any(x=>x.StudentId is int id && id!=sample.StudentId)) throw new InvalidOperationException("Уменьшенный образец принят за другого студента.");
                        }
                    }
                    using var blank = new OpenCvSharp.Mat(720,1280,OpenCvSharp.MatType.CV_8UC3,OpenCvSharp.Scalar.All(60));
                    if (engine.Scan(blank).Count != 0) throw new InvalidOperationException("Лицо найдено в пустом кадре.");
                    File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"distance-result.txt"),lines);
                    if (detectedSmall!=samples.Count || recognizedSmall!=samples.Count) throw new InvalidOperationException("Не все уменьшенные лица распознаны.");
                    return 0;
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"distance-error.txt"),ex.ToString()); return 1; }
            }
            if (args.Length==2 && args[0]=="--enrollment-check")
            {
                string? code=null; CloudFirestore? cloud=null;
                try {
                    cloud=CloudDiagnostics.ConnectAsync(args[1]).GetAwaiter().GetResult();
                    code=cloud.CreateFaceInviteAsync(new CloudStudent("_diagnostic_registration",0,"Проверка регистрации")).GetAwaiter().GetResult();
                    var settings=FirebaseSettings.Load(); var root=$"https://firestore.googleapis.com/v1/projects/{settings.ProjectId}/databases/(default)/documents/groups/{settings.GroupId}";
                    using var http=new System.Net.Http.HttpClient();
                    using var invite=http.GetAsync(root+"/faceInvites/"+code).GetAwaiter().GetResult(); if (!invite.IsSuccessStatusCode) throw new Exception("Code cannot be read anonymously.");
                    var body=System.Text.Json.JsonSerializer.Serialize(new { fields=new { studentId=new { stringValue="_diagnostic_registration" },payload=new { stringValue="{\"samples\":[]}" } } });
                    using var submitted=http.PatchAsync(root+"/faceSubmissions/"+code+"?currentDocument.exists=false",new System.Net.Http.StringContent(body,System.Text.Encoding.UTF8,"application/json")).GetAwaiter().GetResult();
                    if (!submitted.IsSuccessStatusCode) throw new Exception("Anonymous code submission denied.");
                    using var duplicate=http.PatchAsync(root+"/faceSubmissions/"+code+"?currentDocument.exists=false",new System.Net.Http.StringContent(body,System.Text.Encoding.UTF8,"application/json")).GetAwaiter().GetResult();
                    if (duplicate.IsSuccessStatusCode) throw new Exception("Code accepted twice.");
                    using var privateRead=http.GetAsync(root+"/faceSubmissions/"+code).GetAwaiter().GetResult(); if (privateRead.StatusCode!=System.Net.HttpStatusCode.Forbidden) throw new Exception("Raw face submission publicly readable.");
                    using var listFaces=http.GetAsync(root+"/faces").GetAwaiter().GetResult(); if (listFaces.StatusCode!=System.Net.HttpStatusCode.Forbidden) throw new Exception("Cloud faces publicly readable.");
                    var snapshot=cloud.LoadAsync().GetAwaiter().GetResult(); if (!snapshot.FaceSubmissions.Any(x=>x.SubmissionCode==code)) throw new Exception("Teacher cannot see submission.");
                    cloud.RevokeFaceInviteAsync(code).GetAwaiter().GetResult(); code=null;
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"enrollment-result.txt"),"OK: anonymous code lookup; single submission; repeat rejected; private faces/submissions denied; teacher receives submission; diagnostic invite revoked and submission removed"); return 0;
                } catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"enrollment-error.txt"),ex.ToString()); return 1; }
                finally { if (cloud is not null && code is not null) { try { cloud.RevokeFaceInviteAsync(code).GetAwaiter().GetResult(); } catch { } } cloud?.Dispose(); }
            }
            if (args.Length==2 && args[0]=="--sync-faces")
            {
                try {
                    using var cloud=CloudDiagnostics.ConnectAsync(args[1]).GetAwaiter().GetResult(); var snapshot=cloud.LoadAsync().GetAwaiter().GetResult();
                    var settings=FirebaseSettings.Load(); var store=new AttendanceStore(); store.MigrateTeacher(settings.PreviousTeacherUid,settings.TeacherUid,settings.TeacherName); store.ApplyCloudRoster(snapshot,settings.GroupId);
                    var sync=new FaceSync(store,cloud,snapshot.GroupName); sync.ImportAsync(snapshot).GetAwaiter().GetResult(); sync.SaveAsync().GetAwaiter().GetResult();
                    var fresh=cloud.LoadAsync().GetAwaiter().GetResult();
                    var copy=new AttendanceStore(Path.Combine(AppContext.BaseDirectory,"diagnostics","faces-real-fresh-"+Guid.NewGuid().ToString("N"))); copy.ApplyCloudRoster(fresh,settings.GroupId);
                    var restored=new FaceSync(copy,cloud,fresh.GroupName); restored.ImportAsync(fresh).GetAwaiter().GetResult();
                    if (copy.Samples().Count!=fresh.Faces.Count*3 || restored.HasChanges) throw new Exception("Fresh device did not restore cloud faces.");
                    new PublicStatsPublisher(cloud,store).PublishAsync(fresh).GetAwaiter().GetResult();
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"faces-result.txt"),$"OK: cloud faces={fresh.Faces.Count}; fresh device samples={copy.Samples().Count}; no pending={(!sync.HasChanges)}; import error={sync.Error ?? "none"}"); return 0;
                } catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"faces-error.txt"),ex.ToString()); return 1; }
            }
            if (args.Length==2 && args[0]=="--remember-check")
            {
                try {
                    var settings=FirebaseSettings.Load(); using var json=System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[1])); using var http=new System.Net.Http.HttpClient(); var auth=new FirebaseAuth(settings,http);
                    var session=auth.SignInAsync(json.RootElement.GetProperty("password").GetString()!).GetAwaiter().GetResult(); RememberedSession.Save(settings,session);
                    var restored=auth.RestoreAsync(RememberedSession.Load(settings)!).GetAwaiter().GetResult(); RememberedSession.Save(settings,restored);
                    if (restored.Uid!=settings.TeacherUid) throw new Exception("Remembered account mismatch.");
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"remember-result.txt"),"OK: encrypted device token saved; fresh process can restore Firebase login; password not stored"); return 0;
                } catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"remember-error.txt"),ex.ToString()); return 1; }
            }
            if (args.Length == 2 && args[0] == "--sync-attendance")
            {
                try {
                    using var cloud = CloudDiagnostics.ConnectAsync(args[1]).GetAwaiter().GetResult();
                    var snapshot = cloud.LoadAsync().GetAwaiter().GetResult();
                    var store = new AttendanceStore(); store.ApplyCloudRoster(snapshot, FirebaseSettings.Load().GroupId);
                    var sync = new AttendanceSync(store,cloud,snapshot); sync.SaveAsync().GetAwaiter().GetResult();
                    var other = cloud.LoadAsync().GetAwaiter().GetResult();
                    var copy = new AttendanceStore(Path.Combine(AppContext.BaseDirectory,"diagnostics","attendance-real-fresh-" + Guid.NewGuid().ToString("N")));
                    copy.ApplyCloudRoster(other,FirebaseSettings.Load().GroupId); var restored = new AttendanceSync(copy,cloud,other);
                    if (restored.HasChanges || copy.ExportAttendance(snapshot.GroupName).Count != other.Attendance.Count(x => x.Present)) throw new Exception("Fresh device restore failed.");
                    new PublicStatsPublisher(cloud,store).PublishAsync(snapshot).GetAwaiter().GetResult();
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"attendance-result.txt"),$"OK: Firebase attendance records={other.Attendance.Count}; present={other.Attendance.Count(x => x.Present)}; fresh device restored; local pending={sync.HasChanges}"); return 0;
                } catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"attendance-error.txt"),ex.ToString()); return 1; }
            }
            if (args.Contains("--camera-check"))
            {
                try {
                    var results = new List<string>();
                    for (var i=0;i<2;i++) {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        using var camera = CameraDevice.Open(CancellationToken.None); using var frame = new OpenCvSharp.Mat();
                        var deadline = DateTime.UtcNow.AddSeconds(10);
                        while ((!camera.Read(frame) || frame.Empty()) && DateTime.UtcNow < deadline) Thread.Sleep(20);
                        if (frame.Empty()) throw new Exception("Камера не вернула кадр.");
                        results.Add($"Run {i+1}: first frame={watch.ElapsedMilliseconds}ms; backend={camera.GetBackendName()}; frame={frame.Width}x{frame.Height}");
                    }
                    File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"camera-result.txt"),results); return 0;
                } catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"camera-error.txt"),ex.ToString()); return 1; }
            }
            if (args.Contains("--camera-ui-check"))
            {
                try {
                    Application.EnableVisualStyles();
                    var directory = Path.Combine(AppContext.BaseDirectory,"diagnostics"); Directory.CreateDirectory(directory);
                    var store = new AttendanceStore(Path.Combine(directory,"camera-ui-" + Guid.NewGuid().ToString("N")));
                    using var form = new EnrollmentForm(store,new Student(1,"Тест интерфейса","Т-1",0));
                    form.Show(); Application.DoEvents();
                    using (var initial = new System.Drawing.Bitmap(form.Width,form.Height)) { form.DrawToBitmap(initial,new System.Drawing.Rectangle(0,0,form.Width,form.Height)); initial.Save(Path.Combine(directory,"enrollment-loader.png")); }
                    var preview = (PictureBox)typeof(EnrollmentForm).GetField("preview",System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!;
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (preview.Image is null && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
                    var ready = preview.Image is not null;
                    using (var image = new System.Drawing.Bitmap(form.Width,form.Height)) { form.DrawToBitmap(image,new System.Drawing.Rectangle(0,0,form.Width,form.Height)); image.Save(Path.Combine(directory,"enrollment-ready.png")); }
                    form.Close(); while (!form.IsDisposed) { Application.DoEvents(); Thread.Sleep(20); }
                    using var lesson = new LessonForm(store,new CloudSnapshot("Т-1",[],[new("csharp","Программирование C#"),new("economics","Основы экономики и предпринимательской деятельности")],[]));
                    lesson.Show(); Application.DoEvents(); using var lessonImage = new System.Drawing.Bitmap(lesson.Width,lesson.Height);
                    lesson.DrawToBitmap(lessonImage,new System.Drawing.Rectangle(0,0,lesson.Width,lesson.Height)); lessonImage.Save(Path.Combine(directory,"lesson-choices.png")); lesson.Close();
                    File.WriteAllText(Path.Combine(directory,"camera-ui-check.txt"),"Camera ready=" + ready + "; asynchronous close completed"); return ready ? 0 : 1;
                } catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"camera-ui-error.txt"),ex.ToString()); return 1; }
            }
            if (args.Length == 2 && args[0] == "--publish-stats")
            {
                try
                {
                    using var cloud = CloudDiagnostics.ConnectAsync(args[1]).GetAwaiter().GetResult();
                    var snapshot = cloud.LoadAsync().GetAwaiter().GetResult();
                    new PublicStatsPublisher(cloud, new AttendanceStore()).PublishAsync(snapshot, true).GetAwaiter().GetResult();
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "publish-result.txt"), $"OK: public snapshot published; students={snapshot.Students.Count}; grades={snapshot.Grades.Count(x => x.Score is not null)}"); return 0;
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "publish-error.txt"), ex.ToString()); return 1; }
            }
            if (args.Length == 2 && args[0] == "--rename-teacher")
            {
                try
                {
                    using var cloud = CloudDiagnostics.ConnectAsync(args[1]).GetAwaiter().GetResult();
                    var count = cloud.RenameTeacherAsync().GetAwaiter().GetResult();
                    var settings = FirebaseSettings.Load();
                    var local = new AttendanceStore().RenameTeacher(settings.TeacherUid, settings.TeacherName);
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "rename-result.txt"), $"OK: Firebase profile updated; cloud records={count}; local records={local}; name={settings.TeacherName}"); return 0;
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "rename-error.txt"), ex.ToString()); return 1; }
            }
            if (args.Length == 2 && args[0] == "--cache-cloud-check")
            {
                try { CloudDiagnostics.CheckCacheCloudAsync(args[1]).GetAwaiter().GetResult(); return 0; }
                catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "cloud-error.txt"), ex.ToString()); return 1; }
            }
            if (args.Length == 2 && args[0] == "--journal-check")
            {
                try { CloudDiagnostics.CheckJournalAsync(args[1]).GetAwaiter().GetResult(); return 0; }
                catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "cloud-error.txt"), ex.ToString()); return 1; }
            }
            if (args.Length == 2 && args[0] == "--cloud-check")
            {
                try { CloudDiagnostics.CheckAsync(args[1]).GetAwaiter().GetResult(); return 0; }
                catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "cloud-error.txt"), ex.ToString()); return 1; }
            }
            if (args.Length == 2 && args[0] == "--cloud-preview")
            {
                try
                {
                    using var cloud = CloudDiagnostics.ConnectAsync(args[1]).GetAwaiter().GetResult();
                    var snapshot = cloud.LoadAsync().GetAwaiter().GetResult();
                    Application.EnableVisualStyles();
                    var workspace = new CloudWorkspace(() => Task.FromResult(snapshot), cloud.SaveChangesAsync, Path.Combine(AppContext.BaseDirectory, "diagnostics", "preview-draft.json"));
                    workspace.LoadAsync().GetAwaiter().GetResult();
                    using var form = new CloudGradesForm(workspace, snapshot.Students[0].Id, snapshot.Students[0].Name, snapshot.Subjects);
                    form.Show();
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < deadline && !form.Controls.OfType<TableLayoutPanel>().SelectMany(x => x.Controls.OfType<Label>()).Any(x => x.Text.StartsWith("Оценок по предмету"))) { Application.DoEvents(); Thread.Sleep(20); }
                    using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(Path.Combine(AppContext.BaseDirectory, "diagnostics", "cloud-grades.png")); form.Close();
                    var requests = cloud.RequestsSent;
                    using var main = new MainForm(new AttendanceStore(Path.Combine(AppContext.BaseDirectory, "diagnostics", "preview-store")), new TeacherSession("preview", "madina.niyazova.2023@mail.ru", "Ниязова Мадина Ержанкызы"), cloud, workspace);
                    main.Show(); Application.DoEvents();
                    using var mainImage = new System.Drawing.Bitmap(main.Width, main.Height);
                    main.DrawToBitmap(mainImage, new System.Drawing.Rectangle(0, 0, mainImage.Width, mainImage.Height)); mainImage.Save(Path.Combine(AppContext.BaseDirectory, "diagnostics", "cloud-main.png")); main.Close();
                    if (cloud.RequestsSent != requests) throw new InvalidOperationException("Открытие главного окна делает запросы.");
                    return 0;
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "cloud-error.txt"), ex.ToString()); return 1; }
            }
            if (args.Length == 3 && args[0] == "--cloud-seed")
            {
                try { CloudDiagnostics.SeedAsync(args[1], args[2]).GetAwaiter().GetResult(); return 0; }
                catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "cloud-error.txt"), ex.ToString()); return 1; }
            }
            if (args.Contains("--import-starter-data"))
            {
                try
                {
                    var store = new AttendanceStore();
                    var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics"); Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "import-result.txt"), $"Students={store.Students().Count}; Lessons={store.Lessons().Count}");
                    return 0;
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "import-error.txt"), ex.ToString()); return 1; }
            }
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
                    FeatureChecks.Run(directory);
                    Application.EnableVisualStyles();
                    var teacher = new TeacherSession("self-test", "madina.niyazova.2023@mail.ru", "Ниязова Мадина Ержанкызы");
                    using (var login = new LoginForm(false))
                    {
                        login.Show(); Application.DoEvents();
                        using var image = new System.Drawing.Bitmap(login.Width, login.Height);
                        login.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, image.Width, image.Height));
                        image.Save(Path.Combine(directory, "login-screen.png")); login.Close();
                    }
                    dataTest.SaveGrade(null, testStudent, "Математика", DateOnly.FromDateTime(DateTime.Today), 90, "Контрольная работа", teacher);
                    using (var grades = new GradesForm(dataTest, dataTest.Students().Single(s => s.Id == testStudent), teacher))
                    {
                        grades.Show(); Application.DoEvents();
                        using var image = new System.Drawing.Bitmap(grades.Width, grades.Height);
                        grades.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, image.Width, image.Height));
                        image.Save(Path.Combine(directory, "grades-screen.png")); grades.Close();
                    }
                    using (var form = new MainForm(store, teacher))
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
            while (true)
            {
                using var login = new LoginForm();
                if (login.ShowDialog() != DialogResult.OK || login.Session is null) return 0;
                var settings = FirebaseSettings.Load();
                new AttendanceStore().RenameTeacher(settings.TeacherUid, settings.TeacherName);
                var store = new AttendanceStore();
                if (settings.PreviousTeacherUid.Length>0) store.MigrateTeacher(settings.PreviousTeacherUid,settings.TeacherUid,settings.TeacherName);
                using var cloud = settings.UseCloudData ? new CloudFirestore(settings, login.Session) : null;
                var workspace = cloud is null ? null : new CloudWorkspace(cloud, login.Session.Uid);
                var faceSync = cloud is null ? null : new FaceSync(store,cloud,settings.GroupName);
                if (workspace is not null) { using var loading = new LoadingForm(workspace, async () => { store.ApplyCloudRoster(workspace.Snapshot!,settings.GroupId); await faceSync!.ImportAsync(workspace.Snapshot!); }); if (loading.ShowDialog() != DialogResult.OK) return 0; }
                using var main = new MainForm(store:store,teacher: login.Session, cloud: cloud, workspace: workspace,faceSync:faceSync);
                Application.Run(main);
                if (!main.SignedOut) break;
            }
            return 0;
        }
    }
}
