using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel.ChatCompletion;
using NaturalLanguageToSQL.Plugin;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ChatHistoryContainer>();

builder.Services.AddSingleton<Kernel>(serviceProvider =>
{
    var config = serviceProvider.GetRequiredService<IConfiguration>();
    var container = serviceProvider.GetRequiredService<ChatHistoryContainer>();

    // Build kernel
    IKernelBuilder kernelBuilder = Kernel.CreateBuilder();
    kernelBuilder.AddGoogleAIGeminiChatCompletion(
        modelId: "gemini-2.5-flash",
        apiKey: config["Gemini:ApiKey"]
    );
    var kernel = kernelBuilder.Build();

    var plugin = new NlqRetrievalPlugin(
        kernel,
        container.FullChat,
        config,
        container.RecentChat,
        container.SummarizedChat
    );

    kernel.Plugins.AddFromObject(plugin, "NlqRetrievalPlugin");

    return kernel;
});


builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy
            .AllowAnyOrigin()  
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});




builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}


app.UseCors("AllowFrontend");


app.UseHttpsRedirection();
app.MapControllers();
app.Run();

public class ChatHistoryContainer
{
    public ChatHistory FullChat { get; } = new ChatHistory();
    public ChatHistory RecentChat { get; } = new ChatHistory();
    public ChatHistory SummarizedChat { get; } = new ChatHistory();
}