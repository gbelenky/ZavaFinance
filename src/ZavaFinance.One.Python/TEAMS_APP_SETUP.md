# Connect Zava Finance One Python to Teams and Microsoft 365 Copilot

Use [appPackage/python/build/zavafinance-one-python.zip](../../appPackage/python/build/zavafinance-one-python.zip).
Do **not** upload the generic `appPackage.zip` generated beside this guide by `azd`,
or the original/.NET One packages. The branded Python package preserves the
separate Bot routing and finance OAuth identities and the SSO resource alias.

Build/configure through the [Python deployment guide](README.md#separate-deployment).
Revisit that guide after `azd deploy`, which can regenerate generic setup guidance.
Deployment must explicitly target `zavafinance-one-python`, not all services.

For a new installation, in Teams select **Apps > Manage your apps > Upload an app >
Upload a custom app**, choose the branded ZIP, then **Add**. Custom-app availability
is subject to tenant policy; an administrator is needed if upload is disabled.
See [Microsoft's upload guide](https://learn.microsoft.com/microsoftteams/platform/concepts/deploy-and-publish/apps-upload).
Importing the package into Developer Portal alone does not update an existing
personal installation.

Package **1.0.1** is already installed in the demo's M365 Copilot web client, with
initial silent SSO verified. Python **v4** is a server-side update with unchanged
Bot/OAuth/manifest settings: **do not rebuild, upload or reinstall the package**
just for this code update. Use a fresh conversation when testing the current agent.
See the [verification record and limits](README.md#acceptance-and-operational-limits).
