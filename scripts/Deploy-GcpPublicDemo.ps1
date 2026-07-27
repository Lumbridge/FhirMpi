[CmdletBinding()]
param(
    [string] $ProjectId = "ryans-apps",
    [string] $Region = "europe-west2",
    [string] $Dataset = "ryans-dataset",
    [string] $Store = "openmpi-demo",
    [string] $Repository = "openmpi-demo",
    [string] $ServiceName = "openmpi-demo",
    [string] $Tenant = "demo"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-LastExitCode {
    param([string] $Action)

    if ($LASTEXITCODE -ne 0) {
        throw "$Action failed with exit code $LASTEXITCODE."
    }
}

function Test-GcloudResource {
    param([scriptblock] $Command)

    & $Command *> $null
    return $LASTEXITCODE -eq 0
}

foreach ($command in @("gcloud", "docker")) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "'$command' is required but was not found."
    }
}

$account = gcloud auth list --filter=status:ACTIVE --format="value(account)"
Assert-LastExitCode "Reading the active Google Cloud account"
if ([string]::IsNullOrWhiteSpace($account)) {
    throw "No active Google Cloud account is configured."
}

gcloud config set project $ProjectId --quiet
Assert-LastExitCode "Selecting project '$ProjectId'"

gcloud services enable `
    run.googleapis.com `
    healthcare.googleapis.com `
    artifactregistry.googleapis.com `
    secretmanager.googleapis.com `
    --project=$ProjectId `
    --quiet
Assert-LastExitCode "Enabling required Google Cloud APIs"

if (-not (Test-GcloudResource {
            gcloud healthcare datasets describe $Dataset `
                --location=$Region `
                --project=$ProjectId `
                --quiet
        })) {
    gcloud healthcare datasets create $Dataset `
        --location=$Region `
        --project=$ProjectId `
        --quiet
    Assert-LastExitCode "Creating Healthcare API dataset '$Dataset'"
}

if (-not (Test-GcloudResource {
            gcloud healthcare fhir-stores describe $Store `
                --dataset=$Dataset `
                --location=$Region `
                --project=$ProjectId `
                --quiet
        })) {
    gcloud healthcare fhir-stores create $Store `
        --dataset=$Dataset `
        --location=$Region `
        --project=$ProjectId `
        --version=R4 `
        --enable-update-create `
        --quiet
    Assert-LastExitCode "Creating FHIR store '$Store'"
}

$accessToken = gcloud auth print-access-token
Assert-LastExitCode "Obtaining a Google Cloud access token"
$storeResourceName = "projects/$ProjectId/locations/$Region/datasets/$Dataset/fhirStores/$Store"
$storePatchUri = "https://healthcare.googleapis.com/v1/${storeResourceName}?updateMask=complexDataTypeReferenceParsing"
$googleHeaders = @{
    Authorization         = "Bearer $accessToken"
    "X-Goog-User-Project" = $ProjectId
}
$storePatch = @{ complexDataTypeReferenceParsing = "ENABLED" } | ConvertTo-Json -Compress
Invoke-RestMethod `
    -Method Patch `
    -Uri $storePatchUri `
    -Headers $googleHeaders `
    -ContentType "application/json" `
    -Body $storePatch | Out-Null

if (-not (Test-GcloudResource {
            gcloud artifacts repositories describe $Repository `
                --location=$Region `
                --project=$ProjectId `
                --quiet
        })) {
    gcloud artifacts repositories create $Repository `
        --repository-format=docker `
        --immutable-tags `
        --location=$Region `
        --project=$ProjectId `
        --description="OpenMPI public demonstration images" `
        --quiet
    Assert-LastExitCode "Creating Artifact Registry repository '$Repository'"
}

$serviceAccountName = "$ServiceName@$ProjectId.iam.gserviceaccount.com"
if (-not (Test-GcloudResource {
            gcloud iam service-accounts describe $serviceAccountName `
                --project=$ProjectId `
                --quiet
        })) {
    gcloud iam service-accounts create $ServiceName `
        --project=$ProjectId `
        --display-name="OpenMPI public demonstration" `
        --quiet
    Assert-LastExitCode "Creating service account '$serviceAccountName'"
}

gcloud projects add-iam-policy-binding $ProjectId `
    --member="serviceAccount:$serviceAccountName" `
    --role="roles/healthcare.fhirResourceEditor" `
    --quiet | Out-Null
Assert-LastExitCode "Granting FHIR resource access"

$secretName = "$ServiceName-hmac"
if (-not (Test-GcloudResource {
            gcloud secrets describe $secretName `
                --project=$ProjectId `
                --quiet
        })) {
    gcloud secrets create $secretName `
        --replication-policy=automatic `
        --project=$ProjectId `
        --quiet
    Assert-LastExitCode "Creating blocking-key secret '$secretName'"
}

$enabledSecretVersions = gcloud secrets versions list $secretName `
    --project=$ProjectId `
    --filter="state=ENABLED" `
    --format="value(name)"
Assert-LastExitCode "Inspecting blocking-key secret versions"
if ([string]::IsNullOrWhiteSpace(($enabledSecretVersions -join ""))) {
    $randomBytes = [byte[]]::new(32)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($randomBytes)
    $secretValue = [Convert]::ToBase64String($randomBytes)
    $secretPayload = [Convert]::ToBase64String(
        [System.Text.Encoding]::UTF8.GetBytes($secretValue))
    $secretVersionBody = @{
        payload = @{
            data = $secretPayload
        }
    } | ConvertTo-Json -Depth 4 -Compress
    $secretVersionUri =
        "https://secretmanager.googleapis.com/v1/projects/$ProjectId/secrets/${secretName}:addVersion"
    Invoke-RestMethod `
        -Method Post `
        -Uri $secretVersionUri `
        -Headers $googleHeaders `
        -ContentType "application/json" `
        -Body $secretVersionBody | Out-Null
    [Array]::Clear($randomBytes, 0, $randomBytes.Length)
    $secretValue = $null
    $secretPayload = $null
}

gcloud secrets add-iam-policy-binding $secretName `
    --project=$ProjectId `
    --member="serviceAccount:$serviceAccountName" `
    --role="roles/secretmanager.secretAccessor" `
    --quiet | Out-Null
Assert-LastExitCode "Granting secret access"

gcloud auth configure-docker "$Region-docker.pkg.dev" --quiet
Assert-LastExitCode "Configuring Docker authentication"

$imageTag = "demo-" + (Get-Date -Format "yyyyMMddHHmmss")
$registryRoot = "$Region-docker.pkg.dev/$ProjectId/$Repository"
$apiImage = "$registryRoot/api:$imageTag"
$portalImage = "$registryRoot/portal:$imageTag"

docker build -f src/OpenMpi.Api/Dockerfile -t $apiImage .
Assert-LastExitCode "Building the API image"
docker push $apiImage
Assert-LastExitCode "Publishing the API image"
docker build -f src/OpenMpi.Portal/Dockerfile -t $portalImage .
Assert-LastExitCode "Building the portal image"
docker push $portalImage
Assert-LastExitCode "Publishing the portal image"

$tenantSettings = @(
    "RegistryProvider__Type=GcpHealthcare",
    "GcpHealthcare__StoreName=$storeResourceName",
    "Tenants__Items__0__TenantId=$Tenant",
    "Tenants__Items__0__MatchingProfileVersion=uk-demo-v1",
    "Tenants__Items__0__PossibleThreshold=0.62",
    "Tenants__Items__0__ProbableThreshold=0.82",
    "Tenants__Items__0__RequiredLinkApprovals=2",
    "Tenants__Items__0__SourceTrust__pas=100",
    "Tenants__Items__0__SourceTrust__maternity=90",
    "Tenants__Items__0__SourceTrust__emergency=85",
    "Tenants__Items__0__SourceTrust__portal=50",
    "Tenants__Items__0__SourceTrust__demo-source=40",
    "Tenants__Items__0__AuthoritativeSources__0=pas",
    "Tenants__Items__0__BlockingSecrets__0__Version=v1",
    "Tenants__Items__0__BlockingSecrets__0__Active=true"
)
$apiEnvironment = @(
    "DOTNET_ENVIRONMENT=Production",
    "Authentication__Enabled=false",
    "Authentication__DevelopmentTenant=$Tenant",
    "Authentication__DevelopmentSourceSystem=",
    "TenantLimits__ConcurrentRequests=16",
    "TenantLimits__QueueLimit=16"
) + $tenantSettings
$portalEnvironment = @(
    "DOTNET_ENVIRONMENT=Production",
    "PortalAuthentication__Enabled=false",
    "PortalAuthentication__DevelopmentTenant=$Tenant",
    "Portal__SeedSyntheticData=true",
    "Portal__PublicDemo=true",
    "Portal__ManagedSourceSystem=portal",
    "Portal__CircuitRetentionMinutes=3",
    "Portal__OverviewLoadTimeoutSeconds=20"
) + $tenantSettings
$apiEnvironmentSpec = $apiEnvironment -join ","
$portalEnvironmentSpec = $portalEnvironment -join ","
$secretBinding =
    "Tenants__Items__0__BlockingSecrets__0__SecretBase64=${secretName}:latest"

$apiServiceName = "$ServiceName-api"
gcloud run deploy $apiServiceName `
    --project=$ProjectId `
    --region=$Region `
    --image=$apiImage `
    --service-account=$serviceAccountName `
    --allow-unauthenticated `
    --ingress=all `
    --port=8080 `
    --cpu=1 `
    --memory=1Gi `
    --concurrency=40 `
    --timeout=300 `
    --min-instances=0 `
    --max-instances=2 `
    --labels="application=openmpi,environment=public-demo" `
    --set-env-vars=$apiEnvironmentSpec `
    --set-secrets=$secretBinding `
    --startup-probe="httpGet.path=/health/ready,httpGet.port=8080,timeoutSeconds=3,periodSeconds=5,failureThreshold=24" `
    --liveness-probe="httpGet.path=/health/live,httpGet.port=8080,timeoutSeconds=3,periodSeconds=10,failureThreshold=3" `
    --quiet
Assert-LastExitCode "Deploying the public API"

gcloud run deploy $ServiceName `
    --project=$ProjectId `
    --region=$Region `
    --image=$portalImage `
    --service-account=$serviceAccountName `
    --allow-unauthenticated `
    --ingress=all `
    --session-affinity `
    --port=8080 `
    --cpu=1 `
    --memory=1Gi `
    --concurrency=20 `
    --timeout=3600 `
    --min-instances=0 `
    --max-instances=1 `
    --labels="application=openmpi,environment=public-demo" `
    --set-env-vars=$portalEnvironmentSpec `
    --set-secrets=$secretBinding `
    --startup-probe="httpGet.path=/health/ready,httpGet.port=8080,timeoutSeconds=3,periodSeconds=5,failureThreshold=24" `
    --liveness-probe="httpGet.path=/health/live,httpGet.port=8080,timeoutSeconds=3,periodSeconds=10,failureThreshold=3" `
    --quiet
Assert-LastExitCode "Deploying the public portal"

$apiUrl = gcloud run services describe $apiServiceName `
    --project=$ProjectId `
    --region=$Region `
    --format="value(status.url)"
Assert-LastExitCode "Reading the API URL"
$portalUrl = gcloud run services describe $ServiceName `
    --project=$ProjectId `
    --region=$Region `
    --format="value(status.url)"
Assert-LastExitCode "Reading the portal URL"

Invoke-WebRequest -Uri "$apiUrl/health/ready" -TimeoutSec 60 | Out-Null
Invoke-WebRequest -Uri "$portalUrl/health/ready" -TimeoutSec 60 | Out-Null

Write-Output "Public portal: $portalUrl"
Write-Output "FHIR/API:     $apiUrl"
Write-Output "Image tag:    $imageTag"
Write-Warning "This deployment is unauthenticated and contains synthetic data only."
