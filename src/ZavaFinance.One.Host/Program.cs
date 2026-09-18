using Azure.AI.AgentServer.Activity;
using ZavaFinance.Agent;
using ZavaFinance.Channel;
using ZavaFinance.One;

ActivityServer.Run<FinanceActivityApplication>(
    args,
    configureOptions: options => options.DigitalWorker = false,
    configure: builder =>
    {
        Console.WriteLine(
            "ZavaFinance.One.Host starting: native Activity, source dc9cca2d1f1c, no crash recovery.");

        var channelOptions = new ChannelOptions();
        builder.Configuration.GetSection(ChannelOptions.SectionName).Bind(channelOptions);
        builder.Services.AddSingleton(channelOptions);
        builder.Services.AddFinanceServices(builder.Configuration, sessionStoreName: "zavafinance-one-sessions");
        builder.Services.AddFinanceActivity();
    });
