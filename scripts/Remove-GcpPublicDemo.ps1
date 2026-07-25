[CmdletBinding()]
param(
    [string] $ProjectId = "ryans-apps",
    [string] $Region = "europe-west2",
    [string] $Dataset = "ryans-dataset",
    [string] $Store = "fhir-mpi-demo",
    [string] $Repository = "fhir-mpi-demo",
    [string] $ServiceName = "fhir-mpi-demo",
    [switch] $ConfirmRemoval
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $ConfirmRemoval) {
    throw "Pass -ConfirmRemoval to delete the exact FhirMpi public-demo resources."
}

$expectedPrefix = "fhir-mpi-demo"
foreach ($target in @($Store, $Repository, $ServiceName)) {
    if (-not $target.StartsWith($expectedPrefix, [StringComparison]::Ordinal)) {
        throw "Refusing to remove '$target' because it is outside the public-demo naming boundary."
    }
}

$serviceAccountName = "$ServiceName@$ProjectId.iam.gserviceaccount.com"
$secretName = "$ServiceName-hmac"
$apiServiceName = "$ServiceName-api"

gcloud run services delete $apiServiceName `
    --project=$ProjectId `
    --region=$Region `
    --quiet
gcloud run services delete $ServiceName `
    --project=$ProjectId `
    --region=$Region `
    --quiet
gcloud healthcare fhir-stores delete $Store `
    --dataset=$Dataset `
    --location=$Region `
    --project=$ProjectId `
    --quiet
gcloud secrets delete $secretName `
    --project=$ProjectId `
    --quiet
gcloud artifacts repositories delete $Repository `
    --location=$Region `
    --project=$ProjectId `
    --quiet
gcloud iam service-accounts delete $serviceAccountName `
    --project=$ProjectId `
    --quiet

Write-Output "Removed the FhirMpi public-demo services, store, secret, images and service account."
Write-Output "The pre-existing Healthcare dataset '$Dataset' and enabled APIs were preserved."
