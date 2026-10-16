using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Services;

public sealed class AccountService(AppDbContext db, IPasswordHasher<Teacher> hasher)
{
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
    private static readonly Regex LoginPattern = new(@"^[A-Za-z0-9._-]{3,32}$", RegexOptions.CultureInvariant);
    private static readonly SemaphoreSlim accountGate = new(1, 1);

    public async Task<List<TeacherAccountDto>> ListAsync() =>
        (await db.Teachers.AsNoTracking().OrderBy(x => x.FullName).ToListAsync()).Select(Map).ToList();

    public async Task<TemporaryPasswordDto> CreateAsync(SaveTeacherRequest request)
    {
        await accountGate.WaitAsync();
        try
        {
            var (login, name) = await ValidateAsync(request);
            var teacher = new Teacher { Login = login, FullName = name, IsAdmin = request.IsAdmin,
                IsActive = request.IsActive, MustChangePassword = true };
            var password = RandomNumberGenerator.GetString(PasswordAlphabet, 10);
            teacher.PasswordHash = hasher.HashPassword(teacher, password);
            db.Teachers.Add(teacher);
            await db.SaveChangesAsync();
            return new TemporaryPasswordDto(login, password);
        }
        finally { accountGate.Release(); }
    }

    public async Task<TeacherAccountDto> UpdateAsync(int id, SaveTeacherRequest request, int actorId)
    {
        await accountGate.WaitAsync();
        try
        {
            var teacher = await db.Teachers.SingleOrDefaultAsync(x => x.Id == id)
                ?? throw new KeyNotFoundException("Преподаватель не найден.");
            var (login, name) = await ValidateAsync(request, id);
            if (id == actorId && (!request.IsAdmin || !request.IsActive))
                throw new InvalidOperationException("Нельзя снять с себя права администратора или отключить свою учётную запись.");
            if (teacher.IsAdmin && teacher.IsActive && (!request.IsAdmin || !request.IsActive) &&
                !await db.Teachers.AnyAsync(x => x.Id != id && x.IsAdmin && x.IsActive))
                throw new InvalidOperationException("Должен остаться хотя бы один активный администратор.");
            if (teacher.Login != login || teacher.IsAdmin != request.IsAdmin || teacher.IsActive != request.IsActive)
                teacher.SecurityStamp = Guid.NewGuid().ToString("N");
            teacher.Login = login;
            teacher.FullName = name;
            teacher.IsAdmin = request.IsAdmin;
            teacher.IsActive = request.IsActive;
            await db.SaveChangesAsync();
            return Map(teacher);
        }
        finally { accountGate.Release(); }
    }

    public async Task<TemporaryPasswordDto> ResetPasswordAsync(int id, int actorId)
    {
        if (id == actorId) throw new InvalidOperationException("Для своего пароля используйте смену пароля.");
        await accountGate.WaitAsync();
        try
        {
            var teacher = await db.Teachers.SingleOrDefaultAsync(x => x.Id == id)
                ?? throw new KeyNotFoundException("Преподаватель не найден.");
            var password = RandomNumberGenerator.GetString(PasswordAlphabet, 10);
            teacher.PasswordHash = hasher.HashPassword(teacher, password);
            teacher.MustChangePassword = true;
            teacher.SecurityStamp = Guid.NewGuid().ToString("N");
            await db.SaveChangesAsync();
            return new TemporaryPasswordDto(teacher.Login, password);
        }
        finally { accountGate.Release(); }
    }

    private async Task<(string Login, string FullName)> ValidateAsync(SaveTeacherRequest request, int? id = null)
    {
        var login = request.Login.Trim();
        var name = request.FullName.Trim();
        if (!LoginPattern.IsMatch(login)) throw new InvalidOperationException("Логин: 3–32 символа, латинские буквы, цифры, точка, дефис или подчёркивание.");
        if (name.Length is 0 or > 120) throw new InvalidOperationException("Укажите ФИО длиной не более 120 символов.");
        var lower = login.ToLowerInvariant();
        if (await db.Teachers.AnyAsync(x => x.Id != id && x.Login.ToLower() == lower))
            throw new InvalidOperationException("Такой логин уже занят.");
        return (login, name);
    }

    private static TeacherAccountDto Map(Teacher teacher) => new(
        teacher.Id, teacher.Login, teacher.FullName, teacher.IsAdmin, teacher.IsActive,
        teacher.MustChangePassword, teacher.CreatedAt, teacher.LastLoginAt);
}
