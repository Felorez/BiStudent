using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using BiStudent.Contracts;

namespace BiStudent.Client.Tests;

[Collection("settings")]
public sealed class FormsSmokeTests
{
    [Fact]
    public void FormsConstructAndDisposeOnStaThread()
    {
        RunSta(() =>
        {
            var previousPath = ClientSettings.FilePath;
            var directory = Path.Combine(Path.GetTempPath(), "BiStudent.Tests", Guid.NewGuid().ToString("N"));
            try
            {
                ClientSettings.FilePath = Path.Combine(directory, "client.json");
                using var api = new ApiClient(new ClientSettings { ServerUrl = "http://test/" }, new FakeServer());
                using var login = new LoginForm();
                using var password = new ChangePasswordDialog(api, false);
                using var forcedPassword = new ChangePasswordDialog(api, true);
                using var excuse = new ExcuseDialog("Студент", ExcuseReason.Family, true, "  Комментарий  ");
                using var assessment = new AssessmentDialog(api, 1, "Группа", "Предмет", Array.Empty<AssessmentDto>());
                using var teacher = new TeacherDialog(api, null);
                Assert.Equal((ExcuseReason.Family, true, "Комментарий"), excuse.Result);
            }
            finally
            {
                ClientSettings.FilePath = previousPath;
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        });
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
