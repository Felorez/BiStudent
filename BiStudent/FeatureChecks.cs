using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BiStudent;

internal static class FeatureChecks
{
    public static void Run(string directory)
    {
        var dataDirectory = Path.Combine(directory, "grade-test-" + Guid.NewGuid().ToString("N"));
        var store = new AttendanceStore(dataDirectory);
        var student = store.AddStudent("Тест оценок", "Т-1", []);
        var other = store.AddStudent("Другой студент", "Т-1", []);
        var teacher = new TeacherSession("test-uid", "teacher@example.invalid", "Тестовый преподаватель");
        var date = DateOnly.FromDateTime(DateTime.Today);
        var grade = store.SaveGrade(null, student, "Математика", date, 0, "Нулевой балл", teacher);
        var reopened = new AttendanceStore(dataDirectory);
        if (reopened.Grades(student).Single().Score != 0) throw new InvalidOperationException("Нулевая оценка не сохранена.");
        store.SaveGrade(grade, student, "Математика", date, 100, "Исправленная работа", teacher);
        if (store.GradeAverages()[student] != 100 || store.Grades(student).Single().TeacherUid != teacher.Uid)
            throw new InvalidOperationException("Оценка или преподаватель сохранены неверно.");
        foreach (var invalid in new[] { -1, 101 })
        {
            var rejected = false;
            try { store.SaveGrade(null, student, "Математика", date, invalid, "", teacher); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Принята оценка за пределами 0–100.");
        }
        var mismatched = false;
        try { store.SaveGrade(grade, other, "Математика", date, 50, "", teacher); }
        catch (InvalidOperationException) { mismatched = true; }
        if (!mismatched || store.Grades(student).Single().Score != 100) throw new InvalidOperationException("Изменена оценка другого студента.");
        store.DeleteGrade(grade, student);
        if (store.Grades(student).Count != 0) throw new InvalidOperationException("Оценка не удалена.");
        store.SaveGrade(null, student, "Математика", date, 80, "", teacher);
        store.DeleteStudent(student);
        if (store.Grades(student).Count != 0) throw new InvalidOperationException("Оценки удалённого студента остались в базе.");
        CheckAuthAsync().GetAwaiter().GetResult();
        CacheChecks.RunAsync(directory).GetAwaiter().GetResult();
        AttendanceChecks.RunAsync(directory).GetAwaiter().GetResult();
        CheckStarterData(directory);
    }

    private static void CheckStarterData(string directory)
    {
        var data = StarterData.Load(Path.Combine(AppContext.BaseDirectory, "starter-data.json"));
        var store = new AttendanceStore(Path.Combine(directory, "import-test-" + Guid.NewGuid().ToString("N")));
        var first = data.Students.First();
        var studentId = store.AddStudent(first.Name, first.GroupName, []);
        var slot = data.Lessons.First(x => x.Parity == 0 && x.GroupName == first.GroupName);
        store.AddLesson(slot.Subject, slot.GroupName, slot.Day, slot.Start, slot.End);
        var lessonId = store.Lessons().Single().Id;
        var day = DateOnly.FromDateTime(DateTime.Today);
        store.MarkPresent(lessonId, day, studentId, "Тест");
        store.SaveGrade(null, studentId, slot.Subject, day, 75, "До импорта", new TeacherSession("test", "test@example.invalid", "Тест"));
        var result = store.ImportStarterData(data);
        if (result.Students != data.Students.Count - 1 || result.Lessons != data.Lessons.Count - 1 || store.Students().Count != data.Students.Count || store.Lessons().Count != data.Lessons.Count)
            throw new InvalidOperationException("Импорт потерял данные или создал дубликаты.");
        if (store.Grades(studentId).Single().Score != 75 || store.Attendance(lessonId, day, first.GroupName).Single(x => x.StudentId == studentId).SeenAt is null)
            throw new InvalidOperationException("Импорт изменил оценки или посещаемость.");
        if (store.ImportStarterData(data) != (0, 0)) throw new InvalidOperationException("Повторный импорт создал записи.");
        foreach (var lesson in store.Lessons().Where(x => x.Parity != 0))
        {
            var start = data.SemesterStart;
            var weekday = start.AddDays(((int)lesson.Day - (int)start.DayOfWeek + 7) % 7);
            if (lesson.OccursOn(weekday) == lesson.OccursOn(weekday.AddDays(7)) || lesson.OccursOn(weekday) != (lesson.Parity == 1))
                throw new InvalidOperationException("Чётность недели импортирована неверно.");
        }
        foreach (var cancellation in data.Cancellations)
        {
            var source = data.Lessons.Single(x => x.SourceId == cancellation.SourceId);
            if (store.LessonsOn(cancellation.Date).Any(x => x.Subject == source.Subject && x.GroupName == source.GroupName && x.Start == source.Start))
                throw new InvalidOperationException("Импортированная отмена занятия не учитывается.");
        }
    }

    private static async Task CheckAuthAsync()
    {
        var settings = new FirebaseSettings { ApiKey = "test-key", TeacherUid = "allowed-uid", TeacherEmail = "teacher@example.invalid", TeacherName = "Тест" };
        foreach (var scenario in new[] { "success", "other-user", "wrong-password" })
        {
            using var http = new HttpClient(new AuthHandler(scenario));
            var accepted = false;
            try
            {
                var session = await new FirebaseAuth(settings, http).SignInAsync("test-password").ConfigureAwait(false);
                accepted = session.Uid == settings.TeacherUid;
            }
            catch (InvalidOperationException) { }
            if (accepted != (scenario == "success")) throw new InvalidOperationException("Проверка доступа Firebase не прошла: " + scenario);
        }
    }

    private sealed class AuthHandler(string scenario) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/v1/accounts:signInWithPassword")
                throw new InvalidOperationException("Неверный запрос Firebase.");
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (payload.RootElement.GetProperty("email").GetString() != "teacher@example.invalid" || payload.RootElement.GetProperty("password").GetString() != "test-password")
                throw new InvalidOperationException("Неверные данные входа.");
            var body = scenario == "wrong-password" ? "{\"error\":{\"message\":\"INVALID_LOGIN_CREDENTIALS\"}}"
                : JsonSerializer.Serialize(new { localId = scenario == "success" ? "allowed-uid" : "other-uid", email = "teacher@example.invalid" });
            return new HttpResponseMessage(scenario == "wrong-password" ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
