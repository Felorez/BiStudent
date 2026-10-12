using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace BiStudent;

internal sealed record Student(int Id, string Name, string GroupName, int SampleCount, string CloudId = "");
internal sealed record Lesson(int Id, string Subject, string GroupName, DayOfWeek Day, TimeSpan Start, TimeSpan End,
    int Parity = 0, string Room = "", DateOnly? ValidFrom = null, DateOnly? ValidTo = null, DateOnly? OneOffDate = null, DateOnly? SemesterStart = null)
{
    public override string ToString() => $"{Subject} · {GroupName} · {Start:hh\\:mm}–{End:hh\\:mm}";
    public bool OccursOn(DateOnly date)
    {
        if (ValidFrom is { } first && date < first || ValidTo is { } last && date > last) return false;
        if (OneOffDate is { } once) return date == once;
        if (date.DayOfWeek != Day) return false;
        if (Parity == 0) return true;
        var week = (int)Math.Floor((date.DayNumber - (SemesterStart ?? ValidFrom ?? new DateOnly(2026, 9, 1)).DayNumber) / 7d) + 1;
        return Parity == (Math.Abs(week % 2) == 1 ? 1 : 2);
    }
}
internal sealed record AttendanceRow(int StudentId, string Name, string GroupName, DateTime? SeenAt, string Method);
internal sealed record GradeRow(int Id, int StudentId, string Subject, DateOnly Date, int Score, string Comment, string TeacherUid, string TeacherName);

internal sealed class AttendanceStore
{
    public List<PublicAttendance> PublicAttendance(string groupName)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.cloud_id,l.id,l.subject,a.lesson_date,a.seen_at FROM attendance a JOIN students s ON s.id=a.student_id JOIN lessons l ON l.id=a.lesson_id WHERE s.group_name=$group AND l.group_name=$group AND s.cloud_id<>'' ORDER BY a.lesson_date,l.id,s.id";
        command.Parameters.AddWithValue("$group", groupName); using var reader = command.ExecuteReader(); var rows = new List<PublicAttendance>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), DateTime.Parse(reader.GetString(4)).ToString("HH:mm")));
        return rows;
    }
    public int RenameTeacher(string uid, string name)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE grades SET teacher_name=$name WHERE teacher_uid=$uid AND teacher_name IN ('Марал Апай','Марал')";
        command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$uid", uid);
        return command.ExecuteNonQuery();
    }
    private readonly string connectionString;
    private readonly string directory;

    public AttendanceStore(string? dataDirectory = null)
    {
        directory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudentAttendance");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "attendance.db") }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS students (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              name TEXT NOT NULL,
              group_name TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS face_samples (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              student_id INTEGER NOT NULL REFERENCES students(id) ON DELETE CASCADE,
              image BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS lessons (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              subject TEXT NOT NULL,
              group_name TEXT NOT NULL,
              weekday INTEGER NOT NULL,
              start_minutes INTEGER NOT NULL,
              end_minutes INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS attendance (
              lesson_id INTEGER NOT NULL REFERENCES lessons(id) ON DELETE CASCADE,
              lesson_date TEXT NOT NULL,
              student_id INTEGER NOT NULL REFERENCES students(id) ON DELETE CASCADE,
              seen_at TEXT NOT NULL,
              method TEXT NOT NULL,
              PRIMARY KEY(lesson_id, lesson_date, student_id)
            );
            CREATE TABLE IF NOT EXISTS grades (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              student_id INTEGER NOT NULL REFERENCES students(id) ON DELETE CASCADE,
              subject TEXT NOT NULL,
              grade_date TEXT NOT NULL,
              score INTEGER NOT NULL CHECK(score BETWEEN 0 AND 100),
              comment TEXT NOT NULL DEFAULT '',
              teacher_uid TEXT NOT NULL,
              teacher_name TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_grades_student_date ON grades(student_id,grade_date);
            CREATE TABLE IF NOT EXISTS lesson_cancellations (
              lesson_id INTEGER NOT NULL REFERENCES lessons(id) ON DELETE CASCADE,
              lesson_date TEXT NOT NULL,
              reason TEXT NOT NULL,
              PRIMARY KEY(lesson_id,lesson_date)
            );
            CREATE TABLE IF NOT EXISTS data_imports (version TEXT PRIMARY KEY, imported_at TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        using var schema = connection.CreateCommand();
        schema.CommandText = "PRAGMA table_info(face_samples)";
        using var columns = schema.ExecuteReader();
        var hasVersion = false;
        while (columns.Read()) if (columns.GetString(1) == "version") hasVersion = true;
        columns.Close();
        if (!hasVersion)
        {
            using var migration = connection.CreateCommand();
            migration.CommandText = "ALTER TABLE face_samples ADD COLUMN version INTEGER NOT NULL DEFAULT 1";
            migration.ExecuteNonQuery();
        }
        using var lessonSchema = connection.CreateCommand();
        lessonSchema.CommandText = "PRAGMA table_info(lessons)";
        using var lessonColumns = lessonSchema.ExecuteReader();
        var existingColumns = new HashSet<string>();
        while (lessonColumns.Read()) existingColumns.Add(lessonColumns.GetString(1));
        lessonColumns.Close();
        foreach (var (name, definition) in new[] { ("parity", "INTEGER NOT NULL DEFAULT 0"), ("room", "TEXT NOT NULL DEFAULT ''"), ("valid_from", "TEXT"), ("valid_to", "TEXT"), ("one_off_date", "TEXT"), ("semester_start", "TEXT") })
        {
            if (existingColumns.Contains(name)) continue;
            using var migration = connection.CreateCommand(); migration.CommandText = $"ALTER TABLE lessons ADD COLUMN {name} {definition}"; migration.ExecuteNonQuery();
        }
        using var studentSchema = connection.CreateCommand(); studentSchema.CommandText = "PRAGMA table_info(students)";
        using var studentColumns = studentSchema.ExecuteReader(); var hasCloudId = false;
        while (studentColumns.Read()) if (studentColumns.GetString(1) == "cloud_id") hasCloudId = true;
        studentColumns.Close();
        if (!hasCloudId) { using var migration = connection.CreateCommand(); migration.CommandText = "ALTER TABLE students ADD COLUMN cloud_id TEXT NOT NULL DEFAULT ''"; migration.ExecuteNonQuery(); }
        var cloudEnabled = File.Exists(Path.Combine(AppContext.BaseDirectory, "firebase.json")) && FirebaseSettings.Load().UseCloudData;
        if (!cloudEnabled && dataDirectory is null && File.Exists(Path.Combine(AppContext.BaseDirectory, "starter-data.json")))
            ImportStarterData(StarterData.Load(Path.Combine(AppContext.BaseDirectory, "starter-data.json")));
    }

    public (int Students, int Lessons) ImportStarterData(StarterData data)
    {
        if (string.IsNullOrWhiteSpace(data.Version)) throw new InvalidOperationException("Не указана версия импортируемых данных.");
        using var connection = Open();
        using var check = connection.CreateCommand(); check.CommandText = "SELECT COUNT(*) FROM data_imports WHERE version=$version"; check.Parameters.AddWithValue("$version", data.Version);
        if (Convert.ToInt32(check.ExecuteScalar()) > 0) return (0, 0);
        var backupDirectory = Path.Combine(directory, "backups"); Directory.CreateDirectory(backupDirectory);
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(backupDirectory, $"attendance-before-friend-data-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db") }.ToString()))
        { backup.Open(); connection.BackupDatabase(backup); }
        using var transaction = connection.BeginTransaction();
        var addedStudents = 0; var addedLessons = 0;
        foreach (var student in data.Students)
        {
            if (string.IsNullOrWhiteSpace(student.Name) || string.IsNullOrWhiteSpace(student.GroupName)) throw new InvalidOperationException("В списке есть студент без имени или группы.");
            using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO students(name,group_name) SELECT $name,$group WHERE NOT EXISTS(SELECT 1 FROM students WHERE name=$name AND group_name=$group)";
            insert.Parameters.AddWithValue("$name", student.Name.Trim()); insert.Parameters.AddWithValue("$group", student.GroupName.Trim()); addedStudents += insert.ExecuteNonQuery();
        }
        var mapping = new Dictionary<int, int>();
        foreach (var lesson in data.Lessons)
        {
            if (lesson.Start >= lesson.End || lesson.Parity is < 0 or > 2 || string.IsNullOrWhiteSpace(lesson.Subject) || string.IsNullOrWhiteSpace(lesson.GroupName)) throw new InvalidOperationException("В импортируемом расписании некорректное занятие.");
            using var find = connection.CreateCommand(); find.Transaction = transaction;
            find.CommandText = "SELECT id FROM lessons WHERE subject=$subject AND group_name=$group AND weekday=$day AND start_minutes=$start AND end_minutes=$end AND parity=$parity AND (valid_from IS NULL OR valid_from=$from) AND COALESCE(valid_to,'')=$to AND COALESCE(one_off_date,'')=$once LIMIT 1";
            AddLessonParameters(find, lesson, data.SemesterStart);
            var existing = find.ExecuteScalar();
            using var save = connection.CreateCommand(); save.Transaction = transaction;
            save.CommandText = existing is null
                ? "INSERT INTO lessons(subject,group_name,weekday,start_minutes,end_minutes,parity,room,valid_from,valid_to,one_off_date,semester_start) VALUES($subject,$group,$day,$start,$end,$parity,$room,$from,NULLIF($to,''),NULLIF($once,''),$semester); SELECT last_insert_rowid();"
                : "UPDATE lessons SET room=$room,valid_from=$from,valid_to=NULLIF($to,''),one_off_date=NULLIF($once,''),semester_start=$semester WHERE id=$id RETURNING id";
            AddLessonParameters(save, lesson, data.SemesterStart);
            if (existing is not null) save.Parameters.AddWithValue("$id", existing);
            mapping.Add(lesson.SourceId, Convert.ToInt32(save.ExecuteScalar())); if (existing is null) addedLessons++;
        }
        foreach (var cancelled in data.Cancellations)
        {
            if (!mapping.TryGetValue(cancelled.SourceId, out var lessonId)) throw new InvalidOperationException("Отмена ссылается на неизвестное занятие.");
            using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO lesson_cancellations(lesson_id,lesson_date,reason) VALUES($lesson,$date,$reason)";
            insert.Parameters.AddWithValue("$lesson", lessonId); insert.Parameters.AddWithValue("$date", cancelled.Date.ToString("yyyy-MM-dd")); insert.Parameters.AddWithValue("$reason", cancelled.Reason); insert.ExecuteNonQuery();
        }
        using var mark = connection.CreateCommand(); mark.Transaction = transaction; mark.CommandText = "INSERT INTO data_imports(version,imported_at) VALUES($version,$date)";
        mark.Parameters.AddWithValue("$version", data.Version); mark.Parameters.AddWithValue("$date", DateTime.UtcNow.ToString("O")); mark.ExecuteNonQuery();
        transaction.Commit(); return (addedStudents, addedLessons);
    }

    private static void AddLessonParameters(SqliteCommand command, StarterLesson lesson, DateOnly semester)
    {
        command.Parameters.AddWithValue("$subject", lesson.Subject.Trim()); command.Parameters.AddWithValue("$group", lesson.GroupName.Trim()); command.Parameters.AddWithValue("$day", (int)lesson.Day);
        command.Parameters.AddWithValue("$start", (int)lesson.Start.TotalMinutes); command.Parameters.AddWithValue("$end", (int)lesson.End.TotalMinutes); command.Parameters.AddWithValue("$parity", lesson.Parity);
        command.Parameters.AddWithValue("$room", lesson.Room ?? ""); command.Parameters.AddWithValue("$from", lesson.ValidFrom.ToString("yyyy-MM-dd")); command.Parameters.AddWithValue("$to", lesson.ValidTo?.ToString("yyyy-MM-dd") ?? "");
        command.Parameters.AddWithValue("$once", lesson.OneOffDate?.ToString("yyyy-MM-dd") ?? ""); command.Parameters.AddWithValue("$semester", semester.ToString("yyyy-MM-dd"));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public List<GradeRow> Grades(int studentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,student_id,subject,grade_date,score,comment,teacher_uid,teacher_name FROM grades WHERE student_id=$student ORDER BY grade_date DESC,id DESC";
        command.Parameters.AddWithValue("$student", studentId);
        using var reader = command.ExecuteReader();
        var result = new List<GradeRow>();
        while (reader.Read()) result.Add(new GradeRow(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd"), reader.GetInt32(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        return result;
    }

    public Dictionary<int, double> GradeAverages()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT student_id,AVG(score) FROM grades GROUP BY student_id";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<int, double>();
        while (reader.Read()) result.Add(reader.GetInt32(0), reader.GetDouble(1));
        return result;
    }

    public int SaveGrade(int? gradeId, int studentId, string subject, DateOnly date, int score, string comment, TeacherSession teacher)
    {
        if (score is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(score), "Оценка должна быть от 0 до 100.");
        if (string.IsNullOrWhiteSpace(subject) || subject.Trim().Length > 120) throw new ArgumentException("Укажите предмет (до 120 символов).");
        if (comment.Length > 500) throw new ArgumentException("Комментарий должен быть не длиннее 500 символов.");
        if (string.IsNullOrWhiteSpace(teacher.Uid)) throw new ArgumentException("Нужно войти в учётную запись преподавателя.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = gradeId is null
            ? "INSERT INTO grades(student_id,subject,grade_date,score,comment,teacher_uid,teacher_name) VALUES($student,$subject,$date,$score,$comment,$uid,$name); SELECT last_insert_rowid();"
            : "UPDATE grades SET subject=$subject,grade_date=$date,score=$score,comment=$comment,teacher_uid=$uid,teacher_name=$name WHERE id=$id AND student_id=$student RETURNING id";
        if (gradeId is not null) command.Parameters.AddWithValue("$id", gradeId.Value);
        command.Parameters.AddWithValue("$student", studentId);
        command.Parameters.AddWithValue("$subject", subject.Trim());
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$score", score);
        command.Parameters.AddWithValue("$comment", comment.Trim());
        command.Parameters.AddWithValue("$uid", teacher.Uid);
        command.Parameters.AddWithValue("$name", teacher.Name);
        var result = command.ExecuteScalar() ?? throw new InvalidOperationException("Оценка уже удалена. Обновите список.");
        return Convert.ToInt32(result);
    }

    public void DeleteGrade(int gradeId, int studentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM grades WHERE id=$id AND student_id=$student";
        command.Parameters.AddWithValue("$id", gradeId);
        command.Parameters.AddWithValue("$student", studentId);
        command.ExecuteNonQuery();
    }

    public List<Student> Students()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id,s.name,s.group_name,COUNT(f.id),s.cloud_id
            FROM students s LEFT JOIN face_samples f ON f.student_id=s.id AND f.version=2
            GROUP BY s.id ORDER BY s.name COLLATE NOCASE
            """;
        using var reader = command.ExecuteReader();
        var result = new List<Student>();
        while (reader.Read()) result.Add(new Student(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4)));
        return result;
    }

    public void ApplyCloudRoster(CloudSnapshot snapshot, string groupId)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        foreach (var student in snapshot.Students)
        {
            var cloudId = groupId + "/" + student.Id;
            using var find = connection.CreateCommand(); find.Transaction = transaction;
            find.CommandText = "SELECT id FROM students WHERE cloud_id=$cloud OR (name=$name AND group_name=$group) ORDER BY cloud_id DESC LIMIT 1";
            find.Parameters.AddWithValue("$cloud", cloudId); find.Parameters.AddWithValue("$name", student.Name); find.Parameters.AddWithValue("$group", snapshot.GroupName);
            var id = find.ExecuteScalar();
            using var save = connection.CreateCommand(); save.Transaction = transaction;
            save.CommandText = id is null ? "INSERT INTO students(name,group_name,cloud_id) VALUES($name,$group,$cloud)" : "UPDATE students SET name=$name,group_name=$group,cloud_id=$cloud WHERE id=$id";
            if (id is not null) save.Parameters.AddWithValue("$id", id);
            save.Parameters.AddWithValue("$name", student.Name); save.Parameters.AddWithValue("$group", snapshot.GroupName); save.Parameters.AddWithValue("$cloud", cloudId); save.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public int AddStudent(string name, string groupName, IReadOnlyList<byte[]> samples)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO students(name,group_name) VALUES($name,$group); SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$name", name.Trim());
        insert.Parameters.AddWithValue("$group", groupName.Trim());
        var id = Convert.ToInt32(insert.ExecuteScalar());
        foreach (var sample in samples)
        {
            using var image = connection.CreateCommand();
            image.Transaction = transaction;
            image.CommandText = "INSERT INTO face_samples(student_id,image,version) VALUES($id,$image,2)";
            image.Parameters.AddWithValue("$id", id);
            image.Parameters.AddWithValue("$image", ProtectedData.Protect(sample, null, DataProtectionScope.CurrentUser));
            image.ExecuteNonQuery();
        }
        transaction.Commit();
        return id;
    }

    public void DeleteStudent(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM students WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void ReplaceSamples(int studentId, IReadOnlyList<byte[]> samples)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM face_samples WHERE student_id=$id";
            delete.Parameters.AddWithValue("$id", studentId);
            delete.ExecuteNonQuery();
        }
        foreach (var sample in samples)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO face_samples(student_id,image,version) VALUES($id,$image,2)";
            insert.Parameters.AddWithValue("$id", studentId);
            insert.Parameters.AddWithValue("$image", ProtectedData.Protect(sample, null, DataProtectionScope.CurrentUser));
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public List<(int StudentId, byte[] Image)> Samples()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT student_id,image FROM face_samples WHERE version=2";
        using var reader = command.ExecuteReader();
        var result = new List<(int, byte[])>();
        while (reader.Read()) result.Add((reader.GetInt32(0), ProtectedData.Unprotect((byte[])reader[1], null, DataProtectionScope.CurrentUser)));
        return result;
    }

    public List<Lesson> Lessons()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,subject,group_name,weekday,start_minutes,end_minutes,parity,room,valid_from,valid_to,one_off_date,semester_start FROM lessons ORDER BY weekday,start_minutes";
        using var reader = command.ExecuteReader();
        var result = new List<Lesson>();
        while (reader.Read()) result.Add(new Lesson(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), (DayOfWeek)reader.GetInt32(3), TimeSpan.FromMinutes(reader.GetInt32(4)), TimeSpan.FromMinutes(reader.GetInt32(5)), reader.GetInt32(6), reader.GetString(7), ReadDate(reader, 8), ReadDate(reader, 9), ReadDate(reader, 10), ReadDate(reader, 11)));
        return result;
    }

    private static DateOnly? ReadDate(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : DateOnly.ParseExact(reader.GetString(index), "yyyy-MM-dd");

    public List<Lesson> LessonsOn(DateOnly date)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT lesson_id FROM lesson_cancellations WHERE lesson_date=$date"; command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
        using var reader = command.ExecuteReader(); var cancelled = new HashSet<int>(); while (reader.Read()) cancelled.Add(reader.GetInt32(0));
        return Lessons().Where(x => x.OccursOn(date) && !cancelled.Contains(x.Id)).ToList();
    }

    public void AddLesson(string subject, string groupName, DayOfWeek day, TimeSpan start, TimeSpan end)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO lessons(subject,group_name,weekday,start_minutes,end_minutes) VALUES($subject,$group,$day,$start,$end)";
        command.Parameters.AddWithValue("$subject", subject.Trim());
        command.Parameters.AddWithValue("$group", groupName.Trim());
        command.Parameters.AddWithValue("$day", (int)day);
        command.Parameters.AddWithValue("$start", (int)start.TotalMinutes);
        command.Parameters.AddWithValue("$end", (int)end.TotalMinutes);
        command.ExecuteNonQuery();
    }

    public void DeleteLesson(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM lessons WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public bool MarkPresent(int lessonId, DateOnly date, int studentId, string method)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO attendance(lesson_id,lesson_date,student_id,seen_at,method) VALUES($lesson,$date,$student,$seen,$method)";
        command.Parameters.AddWithValue("$lesson", lessonId);
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$student", studentId);
        command.Parameters.AddWithValue("$seen", DateTime.Now.ToString("O"));
        command.Parameters.AddWithValue("$method", method);
        return command.ExecuteNonQuery() > 0;
    }

    public void RemoveMark(int lessonId, DateOnly date, int studentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM attendance WHERE lesson_id=$lesson AND lesson_date=$date AND student_id=$student";
        command.Parameters.AddWithValue("$lesson", lessonId);
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$student", studentId);
        command.ExecuteNonQuery();
    }

    public List<AttendanceRow> Attendance(int lessonId, DateOnly date, string groupName)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id,s.name,s.group_name,a.seen_at,COALESCE(a.method,'')
            FROM students s LEFT JOIN attendance a ON a.student_id=s.id AND a.lesson_id=$lesson AND a.lesson_date=$date
            WHERE s.group_name=$group ORDER BY s.name COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$lesson", lessonId);
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$group", groupName);
        using var reader = command.ExecuteReader();
        var result = new List<AttendanceRow>();
        while (reader.Read()) result.Add(new AttendanceRow(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3)), reader.GetString(4)));
        return result;
    }
}
