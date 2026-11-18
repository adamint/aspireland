# Enable Foundry (optional)

Leave Foundry unconfigured for local development without Azure and for the
[single-host demo](deploy.md). The simulator, Dispatch controls, station boards
and ordinary journey planning don't need it. Anonymous attendees never get
model access, even if the local sites do.

Setting `MetroOps:ProvisionFoundry` to `true` lets Aspire provision a dedicated
Foundry account and three Azure OpenAI deployments: GPT-5 mini with GlobalStandard
for chat, GPT-4o Mini Transcribe with GlobalStandard for audio input, and TTS
with Standard for playback. Each uses the model definition's default capacity
of 1. The AppHost uses
[typed Aspire model definitions](https://aspire.dev/reference/api/csharp/aspire.hosting.foundry/foundryextensions/methods/)
and doesn't reference an account from another app. This provisions model
resources, not the rest of the application.

Use an existing resource group dedicated to Aspireland. Before enabling the
flag, check model availability and quota in the target Azure region, and
review [Azure OpenAI pricing](https://azure.microsoft.com/pricing/details/cognitive-services/openai-service/).
Provisioning consumes quota and inference can incur charges. Use an Entra
identity that can provision the account and deployments in that group and
call inference. Don't point this at a shared demo or production resource group.

Set `AZURE_TENANT`, `AZURE_SUBSCRIPTION`, `AZURE_RESOURCE_GROUP` and
`AZURE_REGION` in your shell. Keep the values out of the repo. Stop this
clone's AppHost first if it's already running.

```sh
az login --tenant "$AZURE_TENANT"
az account set --subscription "$AZURE_SUBSCRIPTION"

env MetroOps__ProvisionFoundry=true \
  "Azure__TenantId=$AZURE_TENANT" \
  "Azure__SubscriptionId=$AZURE_SUBSCRIPTION" \
  "Azure__ResourceGroup=$AZURE_RESOURCE_GROUP" \
  "Azure__Location=$AZURE_REGION" \
  Azure__AllowResourceGroupCreation=false \
  dotnet aspire start --isolated --non-interactive
```

Aspire passes these references to `ai-operations` only. It uses the official
SDK and Azure CLI's Entra credentials, not an API key. Model usage metadata
is observable; prompts, responses and audio aren't captured in telemetry.

## Use existing deployments

Keep `MetroOps:ProvisionFoundry=false` to use an already-provisioned account
without changing Azure resources. Supply private AppHost configuration for
`ConnectionStrings:chat`, `ConnectionStrings:transcription` and
`ConnectionStrings:speech`, plus `Azure:TenantId`. Each connection has this form:

```text
Endpoint=<exact HTTPS Azure account root>/;Deployment=<chat, transcription or speech>
```

Use the exact account endpoint returned by Azure, not a constructed hostname
or a model API path. All three deployments must belong to that same account.
Keep configuration outside Git, preferably in the AppHost's private user secrets
or a scoped private launcher. Only `ai-operations` gets the model references and
tenant; the passenger gateway and web resources don't get model credentials.
Missing deployments leave their corresponding Signal capabilities disabled.

The calling Entra identity needs account-scoped inference access. Allow time for
new role assignments to propagate and verify a real inference call before treating
Signal as ready; an ARM deployment success isn't a data-plane check.

## Try Signal

Try “What's the fastest accessible route from University to the airport?” on
the passenger site, or “Why are Central Exchange queues growing?” in Dispatch.
Signal is the passenger guide. Tap the microphone to start recording and tap
again to stop. By default, the transcript goes into the draft so you can edit
it before sending. Use a reply's speaker button to listen to that reply or
stop it.

Turn on **Voice conversation** to send each recorded turn after transcription
and hear Signal's AI-generated reply. You still start and stop the microphone
each turn. Typed messages don't play automatically.

To turn Foundry off, stop the AppHost and restart with
`MetroOps__ProvisionFoundry=false` and no existing model connection strings.
The flag controls provisioning, not access to existing deployments. Stopping
Aspire doesn't delete the Azure resources. Remove them separately when you no
longer need them.
