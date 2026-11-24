param(
    [ValidateRange(1, 100)]
    [int]$Count = 3,
    [string]$BaseUrl = 'http://localhost:5027'
)

$ErrorActionPreference = 'Stop'

for ($i = 0; $i -lt $Count; $i++) {
    $name = 'Cliente ficticio ' + [Guid]::NewGuid().ToString('N')
    $customer = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/customers" `
        -ContentType 'application/json' -Body (@{ name = $name } | ConvertTo-Json)
    $account = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/accounts" `
        -ContentType 'application/json' -Body (@{ customerId = $customer.id } | ConvertTo-Json)
    Write-Output "Cliente criado: $($customer.id); conta criada: $($account.id)"
    $deposit = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/deposits" `
        -ContentType 'application/json' -Headers @{ 'Idempotency-Key' = "seed-$($account.id)" } `
        -Body (@{ accountId = $account.id; amountMinor = 100000 } | ConvertTo-Json)
    Write-Output "Deposito ficticio criado: $($deposit.id); valor em centavos: $($deposit.amountMinor)"
}
