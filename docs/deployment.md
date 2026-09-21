# Deployment runbook

Choose one language implementation and one environment. This runbook does not
approve infrastructure changes or claim staging/production deployment.
Read [administration requirements](it-admin-catalogue.md) and
[support boundaries](availability-and-support.md) first.

**New customer environment:** complete the
[customer onboarding and local-development runbook](development.md) first.
These commands assume an approved, configured customer azd environment and existing
dependencies. A new environment name alone does not remove repository demonstration
defaults, create a finance database, publish KPIpedia or obtain Entra consent.
There is no assertion here that the customer's greenfield deployment has been tested.

## Deployment units

| Implementation | Source | Service | Code packaging | Teams app package |
| --- | --- | --- | --- | --- |
| [.NET](../src/dotnet/README.md) | `src\dotnet` | `zavafinance-one` | azd publishes `src\dotnet\ZavaFinance.One.Host` as a framework-dependent `linux-x64` bundle | `appPackage\dotnet\build\zavafinance-one.zip` |
| [Python](../src/python/README.md) | `src\python` | `zavafinance-one-python` | Direct-code `python_3_13` remote build | `appPackage\python\build\zavafinance-one-python.zip` |

Both expose native Activity `2.0.0` using BotServiceRbac. Each needs its own Bot,
routing identity, installed package, state store and salt. An approved finance
OAuth client can be reused without sharing the Bots.

## Prepare

1. Select the existing authorized Foundry project and explicit azd environment.
   Verify the account, subscription and service outputs before deployment.
2. Install the chosen language's prerequisites and dependencies. .NET needs the
   exact pinned unofficial Activity feed; Python needs its isolated virtual environment.
3. Validate model/embedding deployments, Search, delegated OAuth, Fabric and KPIpedia
   configuration. Use secure ignored environment configuration for secrets.
4. Confirm the Search ARM resource ID matches the configured resolver endpoint.
   Do not create or select an unrelated Search service by name alone.
5. Preserve deployed `ONE_SESSION_KEY_SALT` / `PYTHON_SESSION_KEY_SALT`.
   A new implementation/environment gets its own random salt; redeployment does not
   rotate it.
6. If initial Bot OAuth configuration is required, have the approved token-exchange
   resource URI and delegated finance API scopes ready. Validate them independently;
   the configuration script will not infer them.

No financial-data permissions are granted by code deployment. User access and
catalogue publication are separately administered.

## Build and deploy one implementation

Run from the repository root. `$environmentName` must be the approved **customer**
azd environment, not the demonstration environment. `$searchResourceId` must contain
the approved ARM ID, not a Search URL. Supply non-secret values from the customer
handover record:

```powershell
$environmentName = Read-Host 'Approved customer azd environment name'
$searchResourceId = Read-Host 'Approved customer Search ARM resource ID'

# .NET: test and stage a local validation bundle; no Azure state is accessed.
.\scripts\deploy-dotnet.ps1 -PublishOnly

# Deploy .NET, including tests, publish, Bot configuration and package generation.
.\scripts\deploy-dotnet.ps1 `
    -EnvironmentName $environmentName `
    -SearchResourceId $searchResourceId

# Or deploy Python, including dependency/test checks and Bot configuration.
.\scripts\deploy-python.ps1 `
    -EnvironmentName $environmentName `
    -SearchResourceId $searchResourceId
```

Run only the chosen deployment command. The wrappers call explicit azd services;
do not use an unscoped deployment to update one implementation. Keep the source
unchanged between successful tests and code packaging.

The examples above assume the named OAuth connection already exists. For a first
deployment, use the [explicit channel-input examples](development.md#first-deployment-and-real-channel-testing),
including the approved `-UserAuthResource` and `-DelegatedScopes` values.

Code deployment does not provision the planned staging/production Cosmos, telemetry,
Key Vault or network resources. Those require an independently approved design and
implemented state adapter.

### .NET hosted-code packaging

In `azure.yaml`, the .NET service's `project` is
`src/dotnet/ZavaFinance.One.Host`, with no `dist` setting. The validated
`azure.ai.agents` **beta.13** packager requires a real project file and independently
runs `dotnet publish -c Release -r linux-x64 --self-contained false`, then ZIPs the
publish output. Its bundled .NET packaging does not consume `dist`.

`deploy-dotnet.ps1 -PublishOnly` produces `.artifacts\dotnet-agent` for local
validation and comparison; azd does **not** deploy that folder directly.
`-NoRestore` affects the wrapper's local steps, not azd's independent packaging.

To inspect the hosted-code package without deploying, run from the repository
root. Keep packaging work inside the repository in this PowerShell process:

```powershell
New-Item -ItemType Directory -Force `
    -Path .\.artifacts\azd-package-work, .\.artifacts\azd-packages | Out-Null
$env:TEMP = $env:TMP = (Resolve-Path .\.artifacts\azd-package-work).Path
azd package zavafinance-one --environment $environmentName --no-prompt `
    --output-path .artifacts\azd-packages\zavafinance-one.zip
```

These are two different ZIPs with different consumers:

| Artifact | Purpose |
| --- | --- |
| `.artifacts\azd-packages\zavafinance-one.zip` | Hosted application code for Foundry |
| `appPackage\dotnet\build\zavafinance-one.zip` | Branded Teams installation manifest and icons |

Do not upload the hosted-code ZIP to Teams. Packaging alone does not deploy the
agent, configure OAuth or install the Teams app.

### Code-only redeployment

After initial onboarding, if identities, OAuth, app manifest and resource configuration
are unchanged, test the chosen variant locally and perform a **targeted** code deployment:

```powershell
# Choose one, not both.
azd deploy zavafinance-one --environment $environmentName --no-prompt
# azd deploy zavafinance-one-python --environment $environmentName --no-prompt
```

This is narrower than the full wrapper: it does not run the repository's channel
reconciliation or branded-package generation. It still performs the Foundry extension's
packaging/deployment work and is a cloud write, not a dry run. Keep the validated source
unchanged, read back the new agent version, and test a fresh conversation. Do not
reprovision shared resources because a generic `azd show` display disagrees with
agent-specific deployment readback.

## Reconcile Bot configuration

After a successful targeted deployment, the wrappers run
[`configure-channel.ps1`](../scripts/configure-channel.ps1). To run it independently:

```powershell
.\scripts\configure-channel.ps1 `
    -Variant DotNet `
    -EnvironmentName $environmentName `
    -SearchResourceId $searchResourceId
```

Use `-Variant Python` for Python. The script:

- Reads the selected service's deployed Bot and runtime identity and verifies its
  exact native Activity endpoint.
- Preserves the selected Bot's existing `mcs` connection without redeploying OAuth.
- Requires explicit `-UserAuthResource` / `-DelegatedScopes` for a missing connection,
  or the corresponding `ONE_` / `PYTHON_` environment settings.
  `-TokenExchangeResource` is an alias for `-UserAuthResource` on the configuration
  and deployment scripts.
- For a new connection, accepts only enabled scopes explicitly exposed on that
  finance API plus `openid`, `profile` and `offline_access`; rejects `.default`,
  wildcards and scopes for another API.
- Validates delegated scopes against the shared finance registration and its
  supported token version. It validates the token-exchange resource independently,
  accepting the selected Bot's approved URI even when that URI is not an identifier
  on the shared finance application. Existing differing overrides fail rather than
  replacing the connection; registration and consent are not modified.
- Adds only missing scoped runtime Search/model grants, applies demo tags and
  builds the selected branded package.

Configuration validation does not certify fresh silent SSO. For a new customer
installation, the documented standard pattern uses the finance SSO registration's
Application ID URI for the token-exchange resource and `webApplicationInfo.resource`.
Any differing per-Bot resource contract needs real-channel identity validation.
Follow the [standalone identity procedure](customer-it-installation-guide.md#8-microsoft-entra-app-registration-and-consent)
rather than copying demonstration identifiers.

`ONE_APP_PACKAGE_VERSION` / `PYTHON_APP_PACKAGE_VERSION` select intentional package
updates. Bot/custom-engine IDs route to the implementation; `webApplicationInfo`
identifies the authorized finance OAuth application and the selected connection's
token-exchange resource. The verified development mappings are in
[operations](operations.md#channel-installation-and-sso-preflight).

Do not write tokens or secrets into command output, package manifests or tracked files.
Use only the selected Bot; configuration is not authority to repoint another service.
For a **missing** `mcs` connection, first-time setup consumes the approved finance
application credential to configure OAuth. That requires an approved identity
administrator/change window; runtime managed-identity federation does not make this separate Bot credential step
secretless. Review the script with that owner before running it in a customer tenant.

### Rebuild an app package without deployment

Use this only with values already verified against the selected Bot and its `mcs`
connection. `$variant` is `DotNet` or `Python`; `$botId` is the Bot application/client
ID, while `$oauthClientId` is the finance OAuth application ID. The package builder
requires these IDs to be **distinct**, with explicit OAuth application/resource values.
`$foundryHostName` is a DNS host name without a scheme or path, and `$oauthResource`
is the full approved `mcs.tokenExchangeUrl`:

```powershell
.\scripts\build-app-package.ps1 `
    -Variant $variant `
    -BotId $botId `
    -AppHostName $foundryHostName `
    -UserAuthAppId $oauthClientId `
    -UserAuthResource $oauthResource
```

Add `-AppVersion $packageVersion` for an intentional three-part package version
update. This builds the corresponding branded ZIP listed above; it does not
deploy code, change OAuth configuration or install/publish the package.

## Install and verify

Upload the chosen branded ZIP using **Teams → Apps → Manage your apps → Upload an
app → Upload a custom app**, then **Add**, subject to tenant policy.
Importing into Developer Portal alone does not update an installed personal app.
Do not upload a generic SDK-generated package that loses finance OAuth settings.

1. Read back deployed runtime/version and the matching Bot endpoint/OAuth settings.
2. Verify readiness and startup dependencies.
3. Confirm installed app identity/version and use a fresh conversation.
4. Test fresh SSO separately from cached authentication; exercise all three tools,
   clicked/typed choices, follow-ups, reset and slow-answer delivery.
5. Validate two-user isolation and reconcile representative statement values.
6. Inspect sanitized telemetry and record remaining gates in [operations](operations.md).

A code-only update with unchanged package/auth configuration does not require a
package reinstall. `reset` clears finance memory, not sign-in or runtime version.

## Rollback and retirement

Keep the approved deployable artifact, package, configuration and catalogue binding
for each release. Roll back only the selected implementation after checking state
schema compatibility and runtime configuration. Preserve its salt and Bot routing.

Before retiring an environment, inventory exclusive versus shared resources and
obtain approval for each deletion. Remove only that environment's app installation,
Bot, hosted deployment and dedicated resources. Retain audit evidence according to
policy; do not delete a shared Foundry project, finance OAuth app, Search service,
Fabric capacity or KPIpedia agent as part of an implementation-level teardown.
