namespace BiStudent.Server.Data;

/// Соответствие локальной строки и документа Firestore.
public sealed class SyncLink
{
    public int Id { get; set; }
    // Имя таблицы EF, оно же имя коллекции.
    public string Table { get; set; } = "";
    public int LocalId { get; set; }
    public string CloudId { get; set; } = "";
    // _updatedAt последней применённой или отправленной версии.
    public DateTimeOffset? CloudUpdatedAt { get; set; }
}

/// Локальное изменение, которое ещё не отправлено в Firestore.
public sealed class SyncOutboxEntry
{
    public int Id { get; set; }
    public string Table { get; set; } = "";
    public int LocalId { get; set; }
    // Заполняется для удалений: строки уже нет, остаётся только id документа.
    public string? CloudId { get; set; }
    public bool Deleted { get; set; }
    public DateTime QueuedAt { get; set; } = DateTime.Now;
    // Почему запись не удаётся отправить (слишком большой документ, нет прав); такие записи ждут ручного разбора.
    public string? Error { get; set; }
}

/// Списанный при слиянии дублей CloudId: ссылки на него из облака ведут на каноническую строку.
public sealed class SyncAlias
{
    public int Id { get; set; }
    public string Table { get; set; } = "";
    public string CloudId { get; set; } = "";
    public string CanonicalCloudId { get; set; } = "";
}
