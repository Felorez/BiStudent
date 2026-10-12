using OpenCvSharp;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BiStudent;

internal sealed record CloudFace(string StudentId, string Version, string Format, List<string> Samples, string UpdateTime = "", string SubmissionCode = "");
internal sealed record FaceStamp(string Hash, string UpdateTime);
internal sealed record FaceState(Dictionary<string,FaceStamp> Baseline, Dictionary<string,string> Codes);
internal sealed class FaceSync
{
    private readonly AttendanceStore store;
    private readonly CloudFirestore cloud;
    private readonly string group;
    private readonly string path;
    private FaceState state;
    public string? Error { get; private set; }
    public FaceSync(AttendanceStore store, CloudFirestore cloud, string group)
    {
        this.store=store; this.cloud=cloud; this.group=group; path=Path.Combine(store.DataDirectory,"face-cloud-state.bin");
        try { state=JsonSerializer.Deserialize<FaceState>(ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser)) ?? new([],[]); }
        catch { state=new([],[]); }
    }
    private static string Hash(List<string> images) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(images))));
    private Dictionary<string,List<string>> Current()
    {
        var students=store.Students().Where(s => s.GroupName==group && s.CloudId.Contains('/')).ToDictionary(x=>x.Id,x=>x.CloudId.Split('/').Last());
        return store.Samples().Where(x=>students.ContainsKey(x.StudentId)).GroupBy(x=>students[x.StudentId]).ToDictionary(x=>x.Key,x=>x.Select(s=>Convert.ToBase64String(s.Image)).ToList());
    }
    private List<CloudFace> Pending() => Current().Where(x=>x.Value.Count==3 && (!state.Baseline.TryGetValue(x.Key,out var stamp) || Hash(x.Value)!=stamp.Hash || state.Codes.ContainsKey(x.Key)))
        .Select(x=>new CloudFace(x.Key,Hash(x.Value),"aligned112",x.Value,state.Baseline.GetValueOrDefault(x.Key)?.UpdateTime ?? "",state.Codes.GetValueOrDefault(x.Key) ?? "")).ToList();
    public bool HasChanges => Pending().Count>0;
    private void SaveState() { File.WriteAllBytes(path+".tmp",ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(state),null,DataProtectionScope.CurrentUser)); File.Move(path+".tmp",path,true); }
    public async Task ImportAsync(CloudSnapshot snapshot)
    {
        await Task.Run(() => {
            Error=null; var pending=Pending().Select(x=>x.StudentId).ToHashSet();
            foreach (var face in snapshot.Faces) {
                if (pending.Contains(face.StudentId) && state.Baseline.ContainsKey(face.StudentId)) continue;
                var student=store.Students().FirstOrDefault(s=>s.CloudId.Split('/').Last()==face.StudentId && s.GroupName==group);
                if (student is null) continue;
                if (face.Format!="aligned112" || face.Samples.Count!=3) { Error="Некорректный облачный образец лица."; continue; }
                try {
                    var bytes=face.Samples.Select(Convert.FromBase64String).ToList();
                    foreach (var image in bytes) { using var mat=Cv2.ImDecode(image,ImreadModes.Color); if (mat.Width!=112 || mat.Height!=112) throw new InvalidOperationException("Некорректный размер образца."); }
                    store.ReplaceSamples(student.Id,bytes); state.Baseline[face.StudentId]=new(Hash(face.Samples),face.UpdateTime);
                } catch { Error="Не удалось загрузить один из образцов лица. Остальные данные доступны."; }
            }
            using var engine=snapshot.FaceSubmissions.Count>0 ? new FaceEngine() : null;
            foreach (var submission in snapshot.FaceSubmissions) {
                if (pending.Contains(submission.StudentId)) continue;
                var student=store.Students().FirstOrDefault(s=>s.CloudId.Split('/').Last()==submission.StudentId && s.GroupName==group);
                if (student is null || submission.Samples.Count!=3) { Error="Отклонён некорректный набор снимков с сайта."; continue; }
                try {
                    var aligned=new List<byte[]>();
                    foreach (var sample in submission.Samples) {
                        var bytes=Convert.FromBase64String(sample); if (bytes.Length>250000) throw new InvalidOperationException("Снимок слишком большой.");
                        using var frame=Cv2.ImDecode(bytes,ImreadModes.Color);
                        if (frame.Empty() || frame.Width>1280 || frame.Height>1280) throw new InvalidOperationException("Некорректный снимок.");
                        aligned.Add(engine!.CaptureSample(frame,requireSingle:true));
                    }
                    store.ReplaceSamples(student.Id,aligned); state.Codes[submission.StudentId]=submission.SubmissionCode;
                } catch { Error=$"Снимки {student.Name} не прошли проверку лица. Выдайте новый код и переснимите лицо."; }
            }
            SaveState();
        });
    }
    public async Task SaveAsync()
    {
        foreach (var face in Pending()) {
            var saved=await cloud.SaveFaceAsync(face);
            state.Baseline[face.StudentId]=new(face.Version,saved.UpdateTime); state.Codes.Remove(face.StudentId); SaveState();
        }
    }
}
