using Azure.AI.AgentServer.Activity;
using ZavaFinance.Agent;
using ZavaFinance.ActivitySupport;
using ZavaFinance.One;

ActivityServer.Run<FinanceActivityApplication>(
    args,
    configureOptions: options => options.DigitalWorker = false,
    configure: builder =>
    {
        Console.WriteLine(
            "ZavaFinance.One.Host starting: native Activity, source dc9cca2d1f1c, no crash recovery.");

        var activityOptions = new ActivityOptions();
        builder.Configuration.GetSection(ActivityOptions.SectionName).Bind(activityOptions);
        builder.Services.AddSingleton(activityOptions);
        builder.Services.AddFinanceServices(builder.Configuration, sessionStoreName: "zavafinance-one-sessions");
        builder.Services.AddFinanceActivity();
    });
