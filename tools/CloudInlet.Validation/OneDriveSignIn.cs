using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Core;
using CloudInlet.Core.OneDrive;

internal static class OneDriveSignIn
{
    public static async Task<int> RunAsync(string[] args)
    {
        var index = Array.IndexOf(args,"--client-id");
        if(index < 0 || index + 1 >= args.Length || !Guid.TryParse(args[index+1],out _))
            throw new ArgumentException("Specify --client-id with the registered public-client application ID.");
        var clientId=args[index+1];
        var tenantIndex=Array.IndexOf(args,"--tenant-id");
        var tenant=tenantIndex>=0 && tenantIndex+1<args.Length ? args[tenantIndex+1] : "common";
        var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CloudBay","Validation","OneDrive");
        var storage=new ClientStorage(directory);
        using var cancellation=new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var id=Guid.NewGuid().ToString("N");
        void SaveProfile(string name, OneDriveTokenSet tokens)
        {
            var profile=new ClientStorage.OneDriveConnection(id,name,clientId,tenant,tokens);
            storage.SaveOneDriveConnections(storage.LoadOneDriveConnections().Where(account=>account.Id!=id).Append(profile).ToArray());
        }
        try
        {
            var auth=new OneDriveAuthClient(clientId,tenant,persist:(tokens,ct)=>
            {
                ct.ThrowIfCancellationRequested();
                SaveProfile("Microsoft test account",tokens);
                return Task.CompletedTask;
            });
            var code=await auth.BeginDeviceSignInAsync(cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { verificationUrl=code.VerificationUri.AbsoluteUri,userCode=code.UserCode,expiresUtc=code.ExpiresUtc }));
            var tokens=await auth.CompleteDeviceSignInAsync(code,cancellation.Token);
            var client=new OneDriveClient(auth);
            var drives=await client.ListDrivesAsync(cancellation.Token);
            // Listing drives may rotate access/refresh tokens; keep the current secure-store token set.
            SaveProfile(drives.FirstOrDefault()?.Owner??"Microsoft test account",storage.LoadOneDriveConnections().Single(account=>account.Id==id).Tokens);
            Console.WriteLine(JsonSerializer.Serialize(new { signedIn=true,accountId=id,driveIds=drives.Select(d=>d.Id).ToArray(),clientData=directory }));
            return 0;
        }
        catch(Exception error)
        {
            Console.Error.WriteLine(error is OneDriveApiException or OneDriveSignInRequiredException ? error.Message : "OneDrive validation sign-in could not complete ("+error.GetType().Name+").");
            return 1;
        }
    }
}
