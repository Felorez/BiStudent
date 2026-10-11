using System.Security.Cryptography;
using BiStudent.Server.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Services;

public sealed class FaceSampleHealth(AppDbContext db, IDataProtectionProvider protection)
{
    public async Task<HashSet<int>> UnreadableStudentsAsync()
    {
        var protector = protection.CreateProtector("BiStudent.FaceSamples");
        var samples = await db.FaceSamples.AsNoTracking()
            .Select(x => new { x.StudentId, x.ProtectedImage }).ToListAsync();
        var unreadable = new HashSet<int>();
        foreach (var group in samples.GroupBy(x => x.StudentId))
        {
            var readable = false;
            foreach (var sample in group)
            {
                try { protector.Unprotect(sample.ProtectedImage); readable = true; break; }
                catch (CryptographicException) { }
            }
            if (!readable) unreadable.Add(group.Key);
        }
        return unreadable;
    }
}
