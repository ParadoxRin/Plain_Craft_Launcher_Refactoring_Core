using System.Net;
using System.Text;
using Pcl.Core;

// Focused privacy contract: empty builds never request authorization; developer
// builds embed only the selected identity and send it through the existing flow.
internal static class AuthPrivacyTests
{
    private const string FixtureId = "11111111-2222-3333-4444-555555555555";
    public static async Task<int> Run(string expected)
    {
        var application = MicrosoftApplication.BuiltIn;
        var requests = 0;
        var expectedId = expected == "configured"
            ? application.RequireClientId()
            : FixtureId;
        using var auth = new MicrosoftAuthentication(
            new Handler(
                async request =>
                {
                    requests++;
                    var form = await request.Content!.ReadAsStringAsync();
                    Require(
                        form.Contains("client_id=" + expectedId),
                        "developer identity reaches request"
                    );
                    Require(
                        !form.Contains("client_secret") && !form.Contains("password"),
                        "no secret or password"
                    );
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"user_code\":\"TEST-CODE\",\"device_code\":\"fixture-device\",\"verification_uri\":\"https://www.microsoft.com/link\",\"expires_in\":900,\"interval\":5}",
                            Encoding.UTF8,
                            "application/json"
                        )
                    };
                }
            )
        );
        if (expected == "empty")
        {
            Require(!application.IsConfigured && application.ClientId == "", "empty embedded default");
            try
            {
                await auth.BeginAsync(
                    application.RequireClientId()
                );
                throw new Exception("Unconfigured login was accepted");
            }
            catch (InvalidOperationException ex)
            {
                Require(
                    ex.Message.Contains("client ID"),
                    "actionable configuration error"
                );
            }

            Require(requests == 0, "unconfigured login makes no network request");
        }
        else
        {
            Require(expected is "fixture" or "configured", "known test mode");
            Require(
                application.RequireClientId() == expectedId,
                "local build config embedded"
            );
            var challenge = await auth.BeginAsync(
                application.RequireClientId()
            );
            Require(
                challenge.UserCode == "TEST-CODE" && requests == 1,
                "existing device flow accepts developer config"
            );
        }

        foreach (var json in new[]
        {
            "{}",
            "{\"clientId\":\"invalid\"}",
            "{\"clientId\":\"\",\"clientSecret\":\"fixture\"}"
        }

        )
        {
            try
            {
                MicrosoftApplication.Parse(json);
                throw new Exception("Invalid/private config accepted");
            }
            catch (InvalidDataException)
            {
                Require(true, "reject invalid or secret-bearing config");
            }
        }

        var offline = AccountManager.OfflineSession("Privacy_Test");
        Require(offline.UserType == "legacy", "offline identity remains available");
        Console.WriteLine("PASS auth privacy " + expected);
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }

        Console.WriteLine("PASS " + message);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return response(request);
        }
    }
}
