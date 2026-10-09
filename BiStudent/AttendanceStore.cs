using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;

namespace BiStudent;

internal sealed record Student(int Id, string Name, string GroupName, int SampleCount);
internal sealed record Lesson(int Id, string Subject, string GroupName, DayOfWeek Day, TimeSpan Start, TimeSpan End)
{
    public override string ToString() => $"{Subject} · {GroupName} · {Start:hh\\:mm}–{End:hh\\:mm}";
}
internal sealed record AttendanceRow(int StudentId, string Name, string GroupName, DateTime? SeenAt, string Method);
internal sealed record JournalRow(DateOnly Date, string Subject, string GroupName, TimeSpan Start, TimeSpan End, string StudentName, DateTime? SeenAt, string Method);

internal sealed class AttendanceStore
{
    private readonly string connectionString;

    public AttendanceStore(string? dataDirectory = null)
    {
        var directory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudentAttendance");
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

    public List<Student> Students()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id,s.name,s.group_name,COUNT(f.id)
            FROM students s LEFT JOIN face_samples f ON f.student_id=s.id AND f.version=2
            GROUP BY s.id ORDER BY s.name COLLATE NOCASE
            """;
        using var reader = command.ExecuteReader();
        var result = new List<Student>();
        while (reader.Read()) result.Add(new Student(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return result;
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
        command.CommandText = "SELECT id,subject,group_name,weekday,start_minutes,end_minutes FROM lessons ORDER BY weekday,start_minutes";
        using var reader = command.ExecuteReader();
        var result = new List<Lesson>();
        while (reader.Read()) result.Add(new Lesson(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), (DayOfWeek)reader.GetInt32(3), TimeSpan.FromMinutes(reader.GetInt32(4)), TimeSpan.FromMinutes(reader.GetInt32(5))));
        return result;
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
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
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
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
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
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$group", groupName);
        using var reader = command.ExecuteReader();
        var result = new List<AttendanceRow>();
        while (reader.Read()) result.Add(new AttendanceRow(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3)), reader.GetString(4)));
        return result;
    }

    public List<JournalRow> Journal()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH sessions AS (SELECT DISTINCT lesson_id, lesson_date FROM attendance)
            SELECT x.lesson_date,l.subject,l.group_name,l.start_minutes,l.end_minutes,s.name,a.seen_at,COALESCE(a.method,'')
            FROM sessions x
            JOIN lessons l ON l.id=x.lesson_id
            JOIN students s ON s.group_name=l.group_name
            LEFT JOIN attendance a ON a.lesson_id=x.lesson_id AND a.lesson_date=x.lesson_date AND a.student_id=s.id
            ORDER BY x.lesson_date,l.start_minutes,l.subject,s.name COLLATE NOCASE
            """;
        using var reader = command.ExecuteReader();
        var result = new List<JournalRow>();
        while (reader.Read()) result.Add(new JournalRow(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture), reader.GetString(1), reader.GetString(2), TimeSpan.FromMinutes(reader.GetInt32(3)), TimeSpan.FromMinutes(reader.GetInt32(4)), reader.GetString(5), reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6)), reader.GetString(7)));
        return result;
    }
}
