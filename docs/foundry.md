# Enable Foundry (optional)

Leave Foundry unconfigured for local development without Azure and for the
[single-host demo](deploy.md). The simulator, Dispatch controls, station boards
and ordinary journey planning don't need it. Anonymous attendees never get
model access, even if the local sites do.

Setting `MetroOps:ProvisionFoundry` to `true` lets Aspire provision a dedicated
Foundry account and three Azure OpenAI deployments: GPT-4.1 for chat,
GPT-4o Mini Transcribe for audio input, and TTS for playback. The AppHost uses
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
`MetroOps__ProvisionFoundry=false`. Stopping Aspire doesn't delete the Azure
resources. Remove them separately when you no longer need them.
