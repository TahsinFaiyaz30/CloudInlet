using System.Text.Json;
using CloudBay.Application;
using CloudBay.Core;
using CloudBay.Core.OneDrive;

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
        try
        {
            var auth=new OneDriveAuthClient(clientId,tenant,persist:(tokens,ct)=>
            {
                ct.ThrowIfCancellationRequested();
                storage.SaveOneDriveConnections([new(id,"Microsoft test account",clientId,tenant,tokens)]);
                return Task.CompletedTask;
            });
            var code=await auth.BeginDeviceSignInAsync(cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { verificationUrl=code.VerificationUri.AbsoluteUri,userCode=code.UserCode,expiresUtc=code.ExpiresUtc }));
            var tokens=await auth.CompleteDeviceSignInAsync(code,cancellation.Token);
            var client=new OneDriveClient(auth);
            var drives=await client.ListDrivesAsync(cancellation.Token);
            storage.SaveOneDriveConnections([new(id,drives.FirstOrDefault()?.Owner??"Microsoft test account",clientId,tenant,tokens)]);
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
