param(
    [string]$BaseUrl = 'http://localhost:5027',
    [ValidateRange(1, 300)]
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$uri = [Uri]$BaseUrl
if (-not $uri.IsAbsoluteUri -or $uri.Scheme -notin @('http', 'https') -or -not $uri.IsLoopback -or
    $uri.UserInfo -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment) {
    throw 'BaseUrl deve ser uma origem HTTP(S) local, sem caminho ou credenciais.'
}
$BaseUrl = $uri.GetLeftPart([UriPartial]::Authority)
$runId = [Guid]::NewGuid().ToString('N')

function Post-Json($path, $body, $key = $null) {
    $parameters = @{
        Method = 'Post'
        Uri = "$BaseUrl$path"
        ContentType = 'application/json'
        Body = ($body | ConvertTo-Json -Compress)
    }
    if ($key) { $parameters.Headers = @{ 'Idempotency-Key' = $key } }
    Invoke-RestMethod @parameters
}

$health = Invoke-RestMethod -Uri "$BaseUrl/health/ready"
if ($health.status -ne 'Healthy') { throw 'Dependencias indisponiveis.' }
$customer = Post-Json '/api/v1/customers' @{ name = "Cliente ficticio $runId" }
$account = Post-Json '/api/v1/accounts' @{ customerId = $customer.id }
Write-Output "Conta ficticia criada: $($account.id)"
$null = Post-Json '/api/v1/deposits' @{ accountId = $account.id; amountMinor = 100000 } "demo-deposit-$runId"
$key = Post-Json '/api/v1/pix/keys' @{ accountId = $account.id }
$incoming = Post-Json '/api/v1/pix/operations' @{
    accountId = $account.id; keyId = $key.id; institutionIspb = '00000000'
    direction = 'Incoming'; amountMinor = 12000; scenario = 'Confirm'
} "demo-incoming-$runId"
$outgoing = Post-Json '/api/v1/pix/operations' @{
    accountId = $account.id; keyId = [Guid]::NewGuid().ToString(); institutionIspb = '00000000'
    direction = 'Outgoing'; amountMinor = 5000; scenario = 'Confirm'
} "demo-outgoing-$runId"

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
foreach ($operation in @($incoming, $outgoing)) {
    do {
        $status = Invoke-RestMethod -Uri "$BaseUrl/api/v1/pix/operations/$($operation.id)"
        if ($status.status -eq 'Confirmed') { break }
        if ($status.status -ne 'Pending') { throw "Pix $($operation.id) terminou em $($status.status)." }
        if ([DateTime]::UtcNow -ge $deadline) { throw "Tempo esgotado aguardando Pix $($operation.id)." }
        Start-Sleep -Seconds 1
    } while ($true)
    $receipt = Invoke-RestMethod -Uri "$BaseUrl/api/v1/pix/operations/$($operation.id)/receipt"
    if ($receipt.operation.status -ne 'Confirmed' -or -not $receipt.settlementEntryId) {
        throw "Comprovante invalido para Pix $($operation.id)."
    }
    Write-Output "Pix confirmado: $($operation.id); lancamento: $($receipt.settlementEntryId)"
}
$result = Invoke-RestMethod -Uri "$BaseUrl/api/v1/accounts/$($account.id)"
if ($result.balanceMinor -ne 107000) { throw "Saldo inesperado: $($result.balanceMinor) centavos." }
Write-Output "Saldo final verificado: $($result.balanceMinor) centavos."
