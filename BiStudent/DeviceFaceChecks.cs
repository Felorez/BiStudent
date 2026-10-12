using OpenCvSharp;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BiStudent;

internal static class DeviceFaceChecks
{
    public static async Task RunAsync(string directory)
    {
        var settings=new FirebaseSettings { ProjectId="test",GroupId="test",TeacherUid="test" };
        var session=new TeacherSession("test","test@example.invalid","Тест","test-token","secret-refresh",DateTimeOffset.UtcNow.AddHours(1));
        var tokenFile=Path.Combine(directory,"device-test-"+Guid.NewGuid().ToString("N"),"session.bin");
        RememberedSession.Save(settings,session,tokenFile);
        if (Encoding.UTF8.GetString(File.ReadAllBytes(tokenFile)).Contains("secret-refresh") || RememberedSession.Load(settings,tokenFile)!="secret-refresh") throw new Exception("Device session is not encrypted or cannot be restored.");
        if (RememberedSession.Load(new FirebaseSettings { ProjectId="other",TeacherUid="test" },tokenFile) is not null) throw new Exception("Cross-project saved session accepted.");
        RememberedSession.Forget(tokenFile); if (File.Exists(tokenFile)) throw new Exception("Signout did not clear device session.");
        var handler=new FaceHandler(); using var cloud=new CloudFirestore(settings,session,new HttpClient(handler));
        var snapshot=new CloudSnapshot("Т-1",[new("s1",1,"Тест")],[],[]);
        var store=new AttendanceStore(Path.Combine(directory,"face-test-"+Guid.NewGuid().ToString("N"))); store.ApplyCloudRoster(snapshot,"test");
        using var image=new Mat(new OpenCvSharp.Size(112,112),MatType.CV_8UC3,new Scalar(20,60,80)); Cv2.ImEncode(".png",image,out var bytes);
        var id=store.Students().Single().Id; store.ReplaceSamples(id,[bytes,bytes,bytes]);
        var sync=new FaceSync(store,cloud,snapshot.GroupName); await sync.ImportAsync(snapshot);
        if (!sync.HasChanges) throw new Exception("Local face not queued for cloud.");
        await sync.SaveAsync(); var requests=handler.Calls;
        for (var i=0;i<10;i++) await sync.SaveAsync();
        if (requests!=1 || handler.Calls!=requests || sync.HasChanges) throw new Exception("Idle face sync sends requests.");
        var remote=snapshot with { Faces=handler.Faces.Values.ToList() };
        var second=new AttendanceStore(Path.Combine(directory,"face-device-"+Guid.NewGuid().ToString("N"))); second.ApplyCloudRoster(remote,"test");
        var restored=new FaceSync(second,cloud,remote.GroupName); await restored.ImportAsync(remote);
        if (restored.HasChanges || second.Samples().Count!=3 || second.Samples().Any(x=>!x.Image.SequenceEqual(bytes))) throw new Exception("Fresh device face restore failed.");
        using var replacement=new Mat(new OpenCvSharp.Size(112,112),MatType.CV_8UC3,new Scalar(60,30,15)); Cv2.ImEncode(".png",replacement,out var changed);
        store.ReplaceSamples(id,[changed,bytes,bytes]); handler.Fail=true;
        try { await sync.SaveAsync(); throw new Exception("Offline face saved unexpectedly."); } catch (HttpRequestException) { }
        var restarted=new FaceSync(store,cloud,snapshot.GroupName); if (!restarted.HasChanges) throw new Exception("Offline face lost on restart.");
        handler.Fail=false; await restarted.SaveAsync();
        if (restarted.HasChanges || handler.Faces.Count!=1) throw new Exception("Face retry duplicated a student.");
        var bad=remote with { FaceSubmissions=[new("s1","code","photos",["bad","bad","bad"],"","code")] };
        await restored.ImportAsync(bad);
        if (restored.Error is null || second.Samples().Count!=3) throw new Exception("Bad website photo replaced valid samples.");
        File.WriteAllText(Path.Combine(directory,"device-face-check.txt"),"OK: DPAPI token roundtrip and signout; face migration; idle zero writes; second device restores 3 samples; offline/restart/retry; malformed website samples preserve valid face");
    }
    private sealed class FaceHandler : HttpMessageHandler
    {
        public int Calls; public bool Fail;
        public Dictionary<string,CloudFace> Faces=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Calls++; if (Fail) throw new HttpRequestException("Offline");
            using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var results=new List<object>();
            foreach (var write in json.RootElement.GetProperty("writes").EnumerateArray()) {
                var update=write.GetProperty("update");
                var face=JsonSerializer.Deserialize<CloudFace>(update.GetProperty("fields").GetProperty("payload").GetProperty("stringValue").GetString()!)!;
                Faces[face.StudentId]=face with { UpdateTime="v"+Calls }; results.Add(new { updateTime="v"+Calls });
            }
            return new(HttpStatusCode.OK) { Content=new StringContent(JsonSerializer.Serialize(new { writeResults=results }),Encoding.UTF8,"application/json") };
        }
    }
}
