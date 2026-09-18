# Connect Zava Finance One to Teams and Microsoft 365 Copilot

Use [appPackage/one/build/zavafinance-one.zip](../../appPackage/one/build/zavafinance-one.zip).
Do **not** upload the generic `appPackage.zip` generated beside this guide by `azd`.
The finance package carries the One branding and preserves the separate messaging
Bot and delegated OAuth application identities.

Build/configure through the [One deployment guide](../ZavaFinance.One/README.md#bot-configuration-and-app-package).
Revisit that guide after `azd deploy`, which can regenerate generic setup guidance.
For this branch, deployment must explicitly target `zavafinance-one`, not all services.

In Teams: **Apps > Manage your apps > Upload an app > Upload a custom app**,
select the branded ZIP, then **Add**. Personal installation is subject to tenant
custom-app policy; absence of the upload option requires your Teams administrator.
See [Microsoft's upload guide](https://learn.microsoft.com/microsoftteams/platform/concepts/deploy-and-publish/apps-upload).

One is installed in the demo's M365 Copilot web client. Interactive sign-in works,
but **initial silent SSO remains unresolved**. Successful upload, cached-token replies
or a healthy endpoint do not establish full channel acceptance. See the
[current acceptance status](../ZavaFinance.One/README.md#deployed-experiment-and-acceptance-status).
