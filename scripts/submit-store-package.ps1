param(
    [Parameter(Mandatory)][string]$PackagePath,
    [string]$ApplicationId = $(if ($env:CLOUDINLET_STORE_APPLICATION_ID) { $env:CLOUDINLET_STORE_APPLICATION_ID } else { $env:CLOUDBAY_STORE_APPLICATION_ID }),
    [string]$TenantId = $(if ($env:CLOUDINLET_STORE_TENANT_ID) { $env:CLOUDINLET_STORE_TENANT_ID } else { $env:CLOUDBAY_STORE_TENANT_ID }),
    [string]$ClientId = $(if ($env:CLOUDINLET_STORE_CLIENT_ID) { $env:CLOUDINLET_STORE_CLIENT_ID } else { $env:CLOUDBAY_STORE_CLIENT_ID }),
    [string]$PackageIdentityName = $(if ($env:CLOUDINLET_STORE_IDENTITY_NAME) { $env:CLOUDINLET_STORE_IDENTITY_NAME } else { $env:CLOUDBAY_STORE_IDENTITY_NAME }),
    [string]$Publisher = $(if ($env:CLOUDINLET_STORE_PUBLISHER) { $env:CLOUDINLET_STORE_PUBLISHER } else { $env:CLOUDBAY_STORE_PUBLISHER }),
    [ValidateSet('Manual', 'Immediate')][string]$PublishMode = 'Manual',
    [switch]$CommitSubmission,
    [Net.Http.HttpMessageHandler]$HttpHandler
)
$ErrorActionPreference = 'Stop'
$storeClientSecret = if ($env:CLOUDINLET_STORE_CLIENT_SECRET) { $env:CLOUDINLET_STORE_CLIENT_SECRET } else { $env:CLOUDBAY_STORE_CLIENT_SECRET }
if ($TenantId -notmatch '^[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}$' -or $ClientId -notmatch '^[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}$' -or $ApplicationId -notmatch '^[A-Za-z0-9]{8,32}$' -or !$storeClientSecret -or !$PackageIdentityName -or !$Publisher) { throw 'Configure the reserved Partner Center package identity/publisher, application ID, Entra tenant/client IDs, and CLOUDINLET_STORE_CLIENT_SECRET. Keep the secret in an environment secret, never a command argument.' }
$package = [IO.Path]::GetFullPath($PackagePath)
if (!(Test-Path -LiteralPath $package -PathType Leaf) -or [IO.Path]::GetExtension($package) -cne '.msix' -or (Get-Item -LiteralPath $package).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Provide a regular production .msix package.' }
if ([IO.Path]::GetFileName($package).Contains('local-validation')) { throw 'Local validation identities cannot be submitted to Microsoft Store.' }
$archiveInspection = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $manifests = @($archiveInspection.Entries | Where-Object { $_.FullName -ceq 'AppxManifest.xml' })
    if ($manifests.Count -ne 1 -or $manifests[0].Length -gt 128KB) { throw 'Provide a Store package containing exactly one valid application manifest.' }
    $manifestStream = $manifests[0].Open()
    $xmlSettings = [Xml.XmlReaderSettings]::new()
    $xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $xmlSettings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($manifestStream, $xmlSettings)
    try {
        $manifest = [Xml.XmlDocument]::new(); $manifest.XmlResolver = $null; $manifest.Load($reader)
        $identity = $manifest.DocumentElement.SelectSingleNode("*[local-name()='Identity']")
        if (!$identity -or $identity.GetAttribute('Name') -cne $PackageIdentityName -or $identity.GetAttribute('Publisher') -cne $Publisher -or $identity.GetAttribute('Name') -ceq 'CloudInlet.LocalValidation' -or $identity.GetAttribute('ProcessorArchitecture') -cne 'x64') { throw 'Provide the production Store package matching the reserved Partner Center identity and publisher.' }
    } finally { $reader.Dispose(); $manifestStream.Dispose() }
} finally { $archiveInspection.Dispose() }
$apiBase = 'https://manage.devcenter.microsoft.com/v1.0/my/applications/' + $ApplicationId
$client = if ($HttpHandler) { [Net.Http.HttpClient]::new($HttpHandler) } else { [Net.Http.HttpClient]::new() }
$client.Timeout = [TimeSpan]::FromMinutes(15)
$token = $null
$temporary = Join-Path ([IO.Path]::GetDirectoryName($package)) ('.submission-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null

function Invoke-StoreApi([string]$Method, [string]$Url, $Body = $null) {
    if ($Url -cne $apiBase -and !$Url.StartsWith($apiBase + '/', [StringComparison]::Ordinal)) { throw 'Unexpected Store API endpoint.' }
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), $Url)
        $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
        if ($null -ne $Body) { $request.Content = [Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 100 -Compress), [Text.Encoding]::UTF8, 'application/json') }
        try {
            $response = $client.SendAsync($request).GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -eq 429 -or [int]$response.StatusCode -eq 503) {
                    if ([int]$response.StatusCode -eq 503 -and $Method -ceq 'POST') { throw 'Store API returned an uncertain mutation result. Inspect the existing Partner Center draft before retrying; no duplicate submission was created by a retry.' }
                    if ($attempt -ge 7) { throw 'Store API remained throttled or unavailable.' }
                    $delay = if ($response.Headers.RetryAfter.Delta) { [Math]::Max(1, [Math]::Ceiling($response.Headers.RetryAfter.Delta.TotalSeconds)) } elseif ($response.Headers.RetryAfter.Date) { [Math]::Max(1, [Math]::Ceiling(($response.Headers.RetryAfter.Date - [DateTimeOffset]::UtcNow).TotalSeconds)) } else { [Math]::Min(60, [Math]::Pow(2, $attempt + 1)) }
                    if ($delay -gt 60) { throw 'Store requested a long cooldown. Rerun the submission workflow after Retry-After; no early retry was sent.' }
                    Start-Sleep -Seconds $delay
                    continue
                }
                if (!$response.IsSuccessStatusCode) { throw "Store API $Method failed (HTTP $([int]$response.StatusCode)). Inspect the draft in Partner Center; no credentials or SAS URLs are logged." }
                $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                if ($json) { return $json | ConvertFrom-Json -Depth 100 }
                return $null
            } finally { $response.Dispose() }
        } finally { $request.Dispose() }
    }
}
try {
    $form = [Collections.Generic.Dictionary[string, string]]::new()
    $form.Add('grant_type', 'client_credentials'); $form.Add('client_id', $ClientId); $form.Add('client_secret', $storeClientSecret); $form.Add('resource', 'https://manage.devcenter.microsoft.com')
    $content = [Net.Http.FormUrlEncodedContent]::new($form)
    try {
        $response = $client.PostAsync("https://login.microsoftonline.com/$TenantId/oauth2/token", $content).GetAwaiter().GetResult()
        try {
            if (!$response.IsSuccessStatusCode) { throw "Partner Center authentication failed (HTTP $([int]$response.StatusCode))." }
            $token = ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).access_token
            if (!$token) { throw 'Partner Center returned no access token.' }
        } finally { $response.Dispose() }
    } finally { $content.Dispose() }
    $app = Invoke-StoreApi 'GET' $apiBase
    if ($app.pendingApplicationSubmission) { throw 'Partner Center already has an in-progress submission. Review or delete that draft before starting another.' }
    $submission = Invoke-StoreApi 'POST' ($apiBase + '/submissions')
    $submissionId = [string]$submission.id
    if ($submissionId -notmatch '^\d+$') { throw 'Partner Center returned an invalid submission identifier.' }
    $uploadUrl = [Uri]$submission.fileUploadUrl
    if ($uploadUrl.Scheme -ne 'https' -or !$uploadUrl.Host.EndsWith('.blob.core.windows.net', [StringComparison]::OrdinalIgnoreCase) -or $uploadUrl.UserInfo) { throw 'Partner Center returned an unexpected package upload endpoint.' }
    $fileName = [IO.Path]::GetFileName($package)
    $packageFolder = Join-Path $temporary 'Payload'
    New-Item -ItemType Directory -Path $packageFolder | Out-Null
    Copy-Item -LiteralPath $package -Destination $packageFolder
    $archive = Join-Path $temporary 'submission.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($packageFolder, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    foreach ($existing in @($submission.applicationPackages)) { $existing.fileStatus = 'PendingDelete' }
    $submission.applicationPackages = @($submission.applicationPackages) + @([PSCustomObject]@{ fileName = $fileName; fileStatus = 'PendingUpload' })
    $submission.targetPublishMode = $PublishMode
    # The API copies the last published listing. Only package replacement and the publish mode are changed.
    Invoke-StoreApi 'PUT' ($apiBase + '/submissions/' + $submissionId) $submission | Out-Null
    $stream = [IO.File]::OpenRead($archive)
    $uploadRequest = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Put, $uploadUrl)
    $uploadRequest.Headers.Add('x-ms-blob-type', 'BlockBlob')
    $uploadRequest.Content = [Net.Http.StreamContent]::new($stream)
    $uploadRequest.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/zip')
    try {
        $uploadResponse = $client.SendAsync($uploadRequest).GetAwaiter().GetResult()
        try { if (!$uploadResponse.IsSuccessStatusCode) { throw "Store package upload failed (HTTP $([int]$uploadResponse.StatusCode)). The draft remains available in Partner Center." } }
        finally { $uploadResponse.Dispose() }
    } finally { $uploadRequest.Dispose(); $stream.Dispose() }
    if (!$CommitSubmission) { Write-Output "Uploaded package to Partner Center draft $submissionId. Commit was not requested."; return }
    Invoke-StoreApi 'POST' ($apiBase + '/submissions/' + $submissionId + '/commit') @{} | Out-Null
    for ($poll = 0; $poll -lt 40; $poll++) {
        $status = Invoke-StoreApi 'GET' ($apiBase + '/submissions/' + $submissionId + '/status')
        if ($status.status -in @('PreProcessing', 'Certification', 'PendingPublication', 'Publishing', 'Published')) { Write-Output "Store submission $submissionId accepted for processing. Status: $($status.status). Certification remains managed by Microsoft."; return }
        if ($status.status -in @('CommitFailed', 'PreProcessingFailed', 'CertificationFailed', 'PublishFailed', 'ReleaseFailed', 'Canceled')) { throw "Store submission $submissionId failed with status $($status.status). Inspect the certification report in Partner Center." }
        Start-Sleep -Seconds 15
    }
    throw "Store submission $submissionId is still processing. Check Partner Center before running another submission."
} catch {
    # HTTP exceptions can contain signed URLs. Preserve only known controlled messages.
    if ($_.Exception.Message -match '^(Store |Partner Center |Configure |Provide |Local validation |Unexpected )') { throw $_.Exception.Message }
    throw 'The Store submission request failed. Inspect the pending submission in Partner Center before retrying; signed upload URLs and credentials are intentionally omitted.'
} finally {
    $client.Dispose()
    $token = $null
    $checked = [IO.Path]::GetFullPath($temporary)
    $parent = [IO.Path]::GetDirectoryName($package).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (!$checked.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Submission staging escaped its package directory.' }
    if (Get-ChildItem -LiteralPath $checked -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Submission cleanup found a linked item.' }
    Remove-Item -LiteralPath $checked -Recurse
}
