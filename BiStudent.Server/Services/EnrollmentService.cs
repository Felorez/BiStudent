using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Services;

public sealed class EnrollmentService(AppDbContext db, RecognitionService recognition, IConfiguration configuration)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int MaximumImageSize = 3 * 1024 * 1024;
    private static string Normalize(string? input) => new((input ?? "").ToUpperInvariant().Where(x => !char.IsWhiteSpace(x) && x != '-').ToArray());
    private static string Format(string code) => $"{code[..4]}-{code[4..]}";

    public Task<EnrollmentCode?> GetActiveAsync(int groupId)
    {
        var now = DateTime.Now;
        return db.EnrollmentCodes.AsNoTracking().Include(x => x.Group)
            .Where(x => x.GroupId == groupId && x.RevokedAt == null && x.ExpiresAt > now)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
    }

    public async Task<EnrollmentCode> CreateAsync(int teacherId, int groupId, int hours)
    {
        if (hours is < 1 or > 168) throw new InvalidOperationException("Срок действия кода — от 1 часа до 7 дней.");
        var group = await db.Groups.SingleOrDefaultAsync(x => x.Id == groupId)
            ?? throw new KeyNotFoundException("Группа не найдена.");
        var now = DateTime.Now;
        var active = await db.EnrollmentCodes.Where(x => x.GroupId == groupId && x.RevokedAt == null && x.ExpiresAt > now).ToListAsync();
        foreach (var old in active) old.RevokedAt = now;
        string code;
        do { code = RandomNumberGenerator.GetString(Alphabet, 8); }
        while (await db.EnrollmentCodes.AnyAsync(x => x.Code == code));
        var result = new EnrollmentCode {
            GroupId = groupId, Group = group, Code = code, CreatedByTeacherId = teacherId,
            CreatedAt = now, ExpiresAt = now.AddHours(hours)
        };
        db.EnrollmentCodes.Add(result);
        await db.SaveChangesAsync();
        return result;
    }

    public async Task RevokeAsync(int groupId)
    {
        var now = DateTime.Now;
        var active = await db.EnrollmentCodes.Where(x => x.GroupId == groupId && x.RevokedAt == null && x.ExpiresAt > now).ToListAsync();
        foreach (var code in active) code.RevokedAt = now;
        await db.SaveChangesAsync();
    }

    public List<string> Links(HttpContext http)
    {
        var configured = configuration["Enrollment:PublicUrl"];
        if (!string.IsNullOrWhiteSpace(configured)) return [configured.Trim().TrimEnd('/') + "/register/"];
        var links = new List<string>();
        void Add(string link) { if (!links.Contains(link, StringComparer.OrdinalIgnoreCase)) links.Add(link); }
        var host = http.Request.Host;
        var localLinks = new List<string>();
        var otherLinks = new List<string>();
        if (host.Host is not ("localhost" or "127.0.0.1"))
            Add($"{http.Request.Scheme}://{host}/register/");
        var port = http.Connection.LocalPort;
        if (port == 0) port = host.Port ?? 80;
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up ||
                network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var address in network.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                    address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) continue;
                var bytes = address.Address.GetAddressBytes();
                var local = bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                    (bytes[0] == 192 && bytes[1] == 168);
                var link = $"http://{address.Address}:{port}/register/";
                if (local) localLinks.Add(link);
                else otherLinks.Add(link);
            }
        }
        foreach (var link in localLinks.Concat(otherLinks)) Add(link);
        if (links.Count == 0) Add($"{http.Request.Scheme}://{host}/register/");
        return links;
    }

    public EnrollmentCodeDto ToDto(EnrollmentCode code, HttpContext http) =>
        new(code.GroupId, code.Group.Name, Format(code.Code), code.CreatedAt, code.ExpiresAt, Links(http));

    private async Task<EnrollmentCode> ValidCodeAsync(string? input)
    {
        var code = Normalize(input);
        var now = DateTime.Now;
        return await db.EnrollmentCodes.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Code == code && x.RevokedAt == null && x.ExpiresAt > now)
            ?? throw new KeyNotFoundException("Код недействителен или срок его действия истёк. Попросите у преподавателя новый код.");
    }

    public async Task<PublicGroupDto> CheckAsync(string code)
    {
        var enrollment = await ValidCodeAsync(code);
        var group = await db.Groups.AsNoTracking().SingleAsync(x => x.Id == enrollment.GroupId);
        var students = await db.Students.AsNoTracking().Where(x => x.GroupId == group.Id).OrderBy(x => x.FullName).ToListAsync();
        var registered = (await db.FaceSamples.AsNoTracking().Where(x => x.Student.GroupId == group.Id)
            .Select(x => x.StudentId).Distinct().ToListAsync()).ToHashSet();
        var pending = (await db.FaceSubmissions.AsNoTracking().Where(x => x.Student.GroupId == group.Id && x.Status == FaceSubmissionStatus.Pending)
            .Select(x => x.StudentId).Distinct().ToListAsync()).ToHashSet();
        return new PublicGroupDto(group.Name, enrollment.ExpiresAt, students.Select(x => new PublicStudentDto(x.Id, x.FullName,
            registered.Contains(x.Id) ? PublicFaceState.Registered : pending.Contains(x.Id) ? PublicFaceState.Pending : PublicFaceState.None)).ToList());
    }

    public async Task<PublicSubmitResultDto> SubmitAsync(PublicSubmitRequest request)
    {
        var code = await ValidCodeAsync(request.Code);
        if (!await db.Students.AnyAsync(x => x.Id == request.StudentId && x.GroupId == code.GroupId))
            throw new InvalidOperationException("Студент не найден в группе.");
        if (!request.Consent) throw new InvalidOperationException("Нужно согласие на обработку биометрических данных.");
        if (request.Images is null || request.Images.Count != 3) throw new InvalidOperationException("Нужно ровно три снимка.");
        var jpegs = new List<byte[]>(3);
        foreach (var image in request.Images)
        {
            byte[] bytes;
            try
            {
                if (string.IsNullOrEmpty(image)) throw new InvalidOperationException("Не удалось прочитать снимок.");
                var source = image.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase) ? image[23..] : image;
                bytes = Convert.FromBase64String(source);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw new InvalidOperationException("Не удалось прочитать снимок.");
            }
            if (bytes.Length > MaximumImageSize) throw new InvalidOperationException("Снимок слишком большой.");
            jpegs.Add(bytes);
        }
        var check = await recognition.CheckSubmissionAsync(request.StudentId, jpegs);
        if (!check.Ok) return new PublicSubmitResultDto(false, check.Message, check.Problems);
        await recognition.RunExclusiveAsync(async () =>
        {
            var pending = await db.FaceSubmissions.Include(x => x.Images)
                .Where(x => x.StudentId == request.StudentId && x.Status == FaceSubmissionStatus.Pending).ToListAsync();
            foreach (var old in pending)
            {
                old.Status = FaceSubmissionStatus.Superseded;
                db.FaceSubmissionImages.RemoveRange(old.Images);
            }
            db.FaceSubmissions.Add(new FaceSubmission {
                StudentId = request.StudentId, EnrollmentCodeId = code.Id, Status = FaceSubmissionStatus.Pending,
                SimilarTo = check.SimilarTo, SimilarScore = check.SimilarScore,
                Images = check.Crops.Select(crop => new FaceSubmissionImage { ProtectedImage = recognition.ProtectImage(crop) }).ToList()
            });
            await db.SaveChangesAsync();
        });
        return new PublicSubmitResultDto(true, "Снимки отправлены. Преподаватель проверит их и подтвердит регистрацию.");
    }

    public Task<List<FaceSubmissionDto>> ListAsync(FaceSubmissionStatus? status)
    {
        var selected = status ?? FaceSubmissionStatus.Pending;
        return db.FaceSubmissions.AsNoTracking().Where(x => x.Status == selected).OrderByDescending(x => x.CreatedAt)
            .Select(x => new FaceSubmissionDto(x.Id, x.StudentId, x.Student.FullName, x.Student.GroupId, x.Student.Group.Name,
                x.CreatedAt, x.Status, x.Images.Count, db.FaceSamples.Any(sample => sample.StudentId == x.StudentId),
                x.SimilarTo, x.SimilarScore, x.RejectReason)).ToListAsync();
    }

    public async Task<byte[]> ImageAsync(int id, int index)
    {
        if (index is < 0 or > 2) throw new KeyNotFoundException("Снимок не найден.");
        var image = await db.FaceSubmissionImages.AsNoTracking().Where(x => x.SubmissionId == id)
            .OrderBy(x => x.Id).Skip(index).FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("Снимок не найден.");
        return recognition.UnprotectImage(image.ProtectedImage);
    }

    public async Task ApproveAsync(int teacherId, int id)
    {
        await recognition.RunExclusiveAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var submission = await db.FaceSubmissions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id)
                ?? throw new KeyNotFoundException("Заявка не найдена.");
            if (submission.Status != FaceSubmissionStatus.Pending) throw new InvalidOperationException("Заявка уже рассмотрена.");
            var reviewedAt = DateTime.Now;
            var updated = await db.FaceSubmissions.Where(x => x.Id == id && x.Status == FaceSubmissionStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, FaceSubmissionStatus.Approved)
                    .SetProperty(x => x.ReviewedAt, reviewedAt)
                    .SetProperty(x => x.ReviewedByTeacherId, teacherId));
            if (updated != 1) throw new InvalidOperationException("Заявка уже рассмотрена.");
            var images = await db.FaceSubmissionImages.Where(x => x.SubmissionId == id).OrderBy(x => x.Id).ToListAsync();
            var crops = images.Select(x => recognition.UnprotectImage(x.ProtectedImage)).ToList();
            var old = await db.FaceSamples.Where(x => x.StudentId == submission.StudentId).ToListAsync();
            db.FaceSamples.RemoveRange(old);
            db.FaceSamples.AddRange(crops.Select(crop => new FaceSample {
                StudentId = submission.StudentId, ProtectedImage = recognition.ProtectImage(crop), CreatedAt = reviewedAt
            }));
            db.FaceSubmissionImages.RemoveRange(images);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            recognition.Invalidate();
        });
    }

    public async Task RejectAsync(int teacherId, int id, string? reason)
    {
        await recognition.RunExclusiveAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var submission = await db.FaceSubmissions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id)
                ?? throw new KeyNotFoundException("Заявка не найдена.");
            if (submission.Status != FaceSubmissionStatus.Pending) throw new InvalidOperationException("Заявка уже рассмотрена.");
            var reviewedAt = DateTime.Now;
            var rejectReason = string.IsNullOrWhiteSpace(reason) ? "Отклонено преподавателем" : reason.Trim()[..Math.Min(reason.Trim().Length, 200)];
            var updated = await db.FaceSubmissions.Where(x => x.Id == id && x.Status == FaceSubmissionStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, FaceSubmissionStatus.Rejected)
                    .SetProperty(x => x.RejectReason, rejectReason)
                    .SetProperty(x => x.ReviewedAt, reviewedAt)
                    .SetProperty(x => x.ReviewedByTeacherId, teacherId));
            if (updated != 1) throw new InvalidOperationException("Заявка уже рассмотрена.");
            var images = await db.FaceSubmissionImages.Where(x => x.SubmissionId == id).ToListAsync();
            db.FaceSubmissionImages.RemoveRange(images);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        });
    }
}
