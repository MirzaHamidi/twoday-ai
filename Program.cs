#pragma warning disable OPENAI001

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenAI.Responses;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

// ======================================================
// BOT / MODEL CONFIG
// ======================================================

const string MODEL = "gpt-5.6-luna";
const string BOT_VERSION = "2.2.0";
const int RECENT_MESSAGE_COUNT = 30;
const int SUMMARIZE_EVERY_HUMAN_MESSAGES = 60;
const int PENDING_ACTION_EXPIRY_MINUTES = 15;
const string DATABASE_FILE = "twoday-ai.db";

string[] BUILD_FEATURES =
[
    "Web araştırması başlamadan önce tahmini maliyet + kullanıcı onayı",
    "Günlük soft/hard API bütçe koruması ve /budget komutları",
    "TwoDay Studio'ya özel bağımsız üçüncü takım arkadaşı profili",
    "Luna / Terra / Sol gibi belirsiz terimlerde sohbet bağlamını koruma",
    "Her yeniden başlatmada selamlama; yeni özellik varsa otomatik changelog",
    "LOW-detail görsel analizi ve görsel kaydı başarısız olursa anlık vision fallback"
];

// ======================================================
// CURRENT PRICING ESTIMATES
// Standard GPT-5.6 Luna: $0.20 input / $1.20 output per 1M tokens
// Web search: $10 / 1k calls = $0.01 per call
// These are estimates used for preflight, not invoice truth.
// ======================================================

const decimal LUNA_INPUT_PER_1M = 0.20m;
const decimal LUNA_OUTPUT_PER_1M = 1.20m;
const decimal WEB_SEARCH_PER_CALL = 0.01m;
const int RESEARCH_WEB_CALL_RESERVE = 2;
const int RESEARCH_SEARCH_CONTENT_TOKEN_RESERVE = 8000;
const int RESEARCH_OUTPUT_TOKEN_RESERVE = 1800;
const int LOW_VISION_TOKEN_RESERVE = 1000;

const decimal DAILY_SOFT_LIMIT_USD = 0.50m;
const decimal DAILY_HARD_LIMIT_USD = 1.00m;

// ======================================================
// ENVIRONMENT
// ======================================================

string telegramToken =
    Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
    ?? throw new Exception("TELEGRAM_BOT_TOKEN bulunamadı.");

string openAiKey =
    Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? throw new Exception("OPENAI_API_KEY bulunamadı.");

string dataDirectory =
    Environment.GetEnvironmentVariable("DATA_DIR")
    ?? AppContext.BaseDirectory;

string? seedDatabaseFile =
    Environment.GetEnvironmentVariable("SEED_DATABASE_FILE");

string? startupChatIdsEnv =
    Environment.GetEnvironmentVariable("STARTUP_CHAT_IDS");

string? adminUserIdsEnv =
    Environment.GetEnvironmentVariable("BOT_ADMIN_USER_IDS");

Directory.CreateDirectory(dataDirectory);

string databaseFile = Path.Combine(dataDirectory, DATABASE_FILE);
string imagesDirectory = Path.Combine(dataDirectory, "images");
Directory.CreateDirectory(imagesDirectory);

if (!File.Exists(databaseFile) &&
    !string.IsNullOrWhiteSpace(seedDatabaseFile) &&
    File.Exists(seedDatabaseFile))
{
    File.Copy(seedDatabaseFile, databaseFile);
    Console.WriteLine($"Eski hafıza {databaseFile} konumuna taşındı.");
}

string connectionString = $"Data Source={databaseFile}";

// ======================================================
// CLIENTS / PROCESS STATE
// ======================================================

var telegram = new TelegramBotClient(telegramToken);
var ai = new ResponsesClient(openAiKey);
var chatLocks = new ConcurrentDictionary<long, SemaphoreSlim>();
var startupGreetingSentThisProcess = new ConcurrentDictionary<long, byte>();
var startupGreetingLock = new SemaphoreSlim(1, 1);
CancellationTokenSource cts = new();

HashSet<long> adminUserIds = ParseLongSet(adminUserIdsEnv);

TimeZoneInfo studioTimeZone;
try
{
    studioTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
}
catch
{
    studioTimeZone = TimeZoneInfo.Utc;
}

// ======================================================
// SAFE SHUTDOWN
// ======================================================

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

// ======================================================
// INITIALIZE
// ======================================================

await InitializeDatabaseAsync();

var me = await telegram.GetMe(cts.Token);
string botUsername = me.Username ?? "";

Console.WriteLine();
Console.WriteLine("====================================");
Console.WriteLine(" TwoDay AI ONLINE");
Console.WriteLine($" @{botUsername}");
Console.WriteLine($" Version: {BOT_VERSION}");
Console.WriteLine($" Model: {MODEL}");
Console.WriteLine($" Database: {databaseFile}");
Console.WriteLine($" Images: {imagesDirectory}");
Console.WriteLine(" Vision: LOW");
Console.WriteLine($" Budget: soft ${DAILY_SOFT_LIMIT_USD:F2} / hard ${DAILY_HARD_LIMIT_USD:F2}");
Console.WriteLine(" Cost confirmation: ENABLED");
Console.WriteLine(" Startup changelog: ENABLED");
Console.WriteLine("====================================");
Console.WriteLine();

// Send restart greeting to configured/recent chats before polling.
try
{
    foreach (long knownChatId in await GetStartupChatIdsAsync())
        await EnsureStartupGreetingAsync(telegram, knownChatId, cts.Token);
}
catch (Exception ex)
{
    Console.WriteLine($"STARTUP GREETING WARNING > {ex.Message}");
}

ReceiverOptions receiverOptions = new()
{
    AllowedUpdates = [UpdateType.Message]
};

telegram.StartReceiving(
    updateHandler: HandleUpdateAsync,
    errorHandler: HandleErrorAsync,
    receiverOptions: receiverOptions,
    cancellationToken: cts.Token
);

Console.WriteLine("Bot mesaj bekliyor.");

try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Bot kapatılıyor.");
}

// ======================================================
// MAIN MESSAGE HANDLER
// ======================================================

async Task HandleUpdateAsync(
    ITelegramBotClient bot,
    Update update,
    CancellationToken cancellationToken)
{
    if (update.Message is not { } message)
        return;

    if (message.From?.IsBot == true)
        return;

    string text = message.Text ?? message.Caption ?? "";
    bool hasText = !string.IsNullOrWhiteSpace(text);
    bool hasPhoto = message.Photo is { Length: > 0 };
    bool hasImageDocument =
        message.Document != null &&
        !string.IsNullOrWhiteSpace(message.Document.MimeType) &&
        message.Document.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    bool hasImage = hasPhoto || hasImageDocument;

    if (!hasText && !hasImage)
        return;

    long chatId = message.Chat.Id;
    long senderId = message.From?.Id ?? 0;
    string senderName = GetSenderName(message.From);

    // If this chat was not known at process start, greet it once now.
    await EnsureStartupGreetingAsync(bot, chatId, cancellationToken);

    ImagePayload? image = null;

    if (hasImage)
    {
        try
        {
            image = await DownloadImageAsync(bot, message, chatId, cancellationToken);
            Console.WriteLine($"[{message.Chat.Title ?? chatId.ToString()}] {senderName}: [GÖRSEL] {text}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"IMAGE DOWNLOAD ERROR > {ex}");
            await bot.SendMessage(
                chatId: chatId,
                text: "Görseli Telegram'dan indirirken bir sorun yaşadım.",
                cancellationToken: cancellationToken);
            return;
        }
    }
    else
    {
        Console.WriteLine($"[{message.Chat.Title ?? chatId.ToString()}] {senderName}: {text}");
    }

    string databaseMessage = image != null
        ? $"[GÖRSEL]{(string.IsNullOrWhiteSpace(text) ? "" : $" Caption: {text}")}"
        : text;

    await SaveMessageAsync(chatId, senderId, senderName, "user", databaseMessage);

    var chatLock = chatLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
    await chatLock.WaitAsync(cancellationToken);

    try
    {
        string normalized = NormalizeCommandText(text);

        // ---------------- BUDGET COMMANDS ----------------
        if (normalized is "/budget" or "budget")
        {
            await SendBudgetStatusAsync(bot, chatId, cancellationToken);
            return;
        }

        if (normalized is "/budget unlock" or "budget unlock")
        {
            if (!CanManageBudget(senderId))
            {
                await bot.SendMessage(chatId, "Bu komut için yetkin yok.", cancellationToken: cancellationToken);
                return;
            }

            await SetBudgetUnlockedAsync(chatId, true);
            await bot.SendMessage(
                chatId,
                "🔓 Günlük hard budget limiti bugün için açıldı. Araştırmalar yine işlem bazında onay isteyecek.",
                cancellationToken: cancellationToken);
            return;
        }

        if (normalized is "/budget lock" or "budget lock")
        {
            if (!CanManageBudget(senderId))
            {
                await bot.SendMessage(chatId, "Bu komut için yetkin yok.", cancellationToken: cancellationToken);
                return;
            }

            await SetBudgetUnlockedAsync(chatId, false);
            await bot.SendMessage(chatId, "🔒 Günlük hard budget limiti tekrar aktif.", cancellationToken: cancellationToken);
            return;
        }

        // ---------------- PENDING ACTION CONFIRM/CANCEL ----------------
        PendingAction? pending = await GetPendingActionAsync(chatId, senderId);

        if (pending != null)
        {
            if (IsCancellationText(text))
            {
                await DeletePendingActionAsync(pending.Id);
                await bot.SendMessage(chatId, "Tamam, işlemi iptal ettim. 👍", cancellationToken: cancellationToken);
                return;
            }

            if (IsConfirmationText(text))
            {
                decimal currentSpend = await GetTodayEstimatedSpendAsync(chatId);
                decimal projectedSpend = currentSpend + pending.EstimatedCostUsd;
                bool budgetUnlocked = await IsBudgetUnlockedAsync(chatId);

                if (projectedSpend > DAILY_HARD_LIMIT_USD && !budgetUnlocked)
                {
                    await bot.SendMessage(
                        chatId,
                        $"⛔ Bu işlem günlük hard limite takılıyor.\n\n" +
                        $"Bugün: ~${currentSpend:F4}\n" +
                        $"Bu işlem: ~${pending.EstimatedCostUsd:F4}\n" +
                        $"İşlem sonrası: ~${projectedSpend:F4}\n" +
                        $"Hard limit: ${DAILY_HARD_LIMIT_USD:F2}\n\n" +
                        $"Yetkili kullanıcı /budget unlock yazıp ardından tekrar evet diyebilir.",
                        cancellationToken: cancellationToken);
                    return;
                }

                // IMPORTANT: expensive action can only enter here after explicit confirmation
                // by the SAME user in the SAME chat.
                await ExecuteConfirmedPendingActionAsync(bot, pending, cancellationToken);
                return;
            }
        }

        bool isResearch = IsResearchRequest(text);
        bool directlyMentioned = IsBotMentioned(text, botUsername);

        // ---------------- EXPENSIVE ACTION PREFLIGHT ----------------
        // Research never executes in this branch. It only creates a PendingAction.
        if (isResearch)
        {
            ResearchCostEstimate estimate =
                await EstimateResearchCostAsync(chatId, text, image != null);

            await CreatePendingActionAsync(
                chatId: chatId,
                userId: senderId,
                actionType: "research",
                originalRequest: text,
                imagePath: image?.LocalPath,
                estimatedInputTokens: estimate.InputTokens,
                estimatedOutputTokens: estimate.OutputTokens,
                estimatedWebCalls: estimate.WebCalls,
                estimatedCostUsd: estimate.TotalCostUsd);

            decimal todaySpend = await GetTodayEstimatedSpendAsync(chatId);
            decimal projected = todaySpend + estimate.TotalCostUsd;
            bool budgetUnlocked = await IsBudgetUnlockedAsync(chatId);

            StringBuilder confirmation = new();
            confirmation.AppendLine("💸 Bu işlem web araştırması kullanacak.");
            confirmation.AppendLine();
            confirmation.AppendLine($"Tahmini input: ~{estimate.InputTokens:N0} token");
            confirmation.AppendLine($"Tahmini output: ~{estimate.OutputTokens:N0} token");
            confirmation.AppendLine($"Web search rezervi: ~{estimate.WebCalls} call");
            confirmation.AppendLine();
            confirmation.AppendLine($"Model maliyeti: ~${estimate.ModelCostUsd:F4}");
            confirmation.AppendLine($"Web search maliyeti: ~${estimate.WebCostUsd:F4}");
            confirmation.AppendLine($"Tahmini toplam: ~${estimate.TotalCostUsd:F4}");
            confirmation.AppendLine();
            confirmation.AppendLine($"Bugün: ~${todaySpend:F4} → ~${projected:F4}");

            if (image != null && string.IsNullOrWhiteSpace(image.LocalPath))
            {
                confirmation.AppendLine();
                confirmation.AppendLine("⚠️ Görsel anlık olarak okunabiliyor ama diske kaydedilemedi; onay sonrası research çağrısına görseli tekrar ekleyemeyebilirim.");
            }

            if (projected > DAILY_SOFT_LIMIT_USD)
            {
                confirmation.AppendLine();
                confirmation.AppendLine($"⚠️ Soft limit (${DAILY_SOFT_LIMIT_USD:F2}) aşılacak.");
            }

            if (projected > DAILY_HARD_LIMIT_USD && !budgetUnlocked)
            {
                confirmation.AppendLine();
                confirmation.AppendLine($"⛔ Hard limit (${DAILY_HARD_LIMIT_USD:F2}) nedeniyle şu an çalıştıramam.");
                confirmation.AppendLine("Yetkili kullanıcı önce /budget unlock yazabilir.");
            }
            else
            {
                confirmation.AppendLine();
                confirmation.AppendLine("Devam edeyim mi?");
                confirmation.AppendLine("evet / hayır");
            }

            confirmation.AppendLine();
            confirmation.AppendLine("Not: Bu tahmindir; gerçek kullanım web içeriği ve model çıktısına göre değişebilir.");

            await bot.SendMessage(chatId, confirmation.ToString(), cancellationToken: cancellationToken);
            return;
        }

        // ---------------- NORMAL CHAT ----------------
        bool shouldReply = hasImage || directlyMentioned;

        if (!shouldReply)
            shouldReply = await ShouldAiReplyAsync(chatId, senderId, cancellationToken);

        if (!shouldReply)
        {
            Console.WriteLine("AI > sessiz kaldı");
            await MaybeUpdateSummaryAsync(chatId, cancellationToken);
            return;
        }

        await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: cancellationToken);

        string answer = await GenerateNormalResponseAsync(
            chatId,
            senderId,
            image?.Data,
            cancellationToken);

        await SendAndStoreAssistantAnswerAsync(bot, chatId, answer, cancellationToken);
        await MaybeUpdateSummaryAsync(chatId, cancellationToken);
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception ex)
    {
        Console.WriteLine($"AI ERROR > {ex}");
        try
        {
            await bot.SendMessage(
                chatId,
                "Bir hata yaşadım. Loga düştü; birazdan tekrar deneyelim.",
                cancellationToken: cancellationToken);
        }
        catch
        {
        }
    }
    finally
    {
        chatLock.Release();
    }
}

// ======================================================
// STRICT CONFIRMED ACTION EXECUTION
// ======================================================

async Task ExecuteConfirmedPendingActionAsync(
    ITelegramBotClient bot,
    PendingAction pending,
    CancellationToken cancellationToken)
{
    if (pending.ActionType != "research")
    {
        await DeletePendingActionAsync(pending.Id);
        return;
    }

    BinaryData? imageData = null;
    if (!string.IsNullOrWhiteSpace(pending.ImagePath))
        imageData = await LoadImageBinaryDataAsync(pending.ImagePath);

    await bot.SendMessage(
        pending.ChatId,
        "🔎 Onaylandı. Şimdi web araştırmasına başlıyorum.",
        cancellationToken: cancellationToken);

    await bot.SendChatAction(
        pending.ChatId,
        ChatAction.Typing,
        cancellationToken: cancellationToken);

    // There is intentionally no other call site for GenerateResearchResponseAsync.
    string answer = await GenerateResearchResponseAsync(
        pending.ChatId,
        pending.OriginalRequest,
        imageData,
        cancellationToken);

    await SendAndStoreAssistantAnswerAsync(bot, pending.ChatId, answer, cancellationToken);

    await RecordCostEstimateAsync(
        pending.ChatId,
        pending.UserId,
        "research",
        pending.EstimatedInputTokens,
        pending.EstimatedOutputTokens,
        pending.EstimatedWebCalls,
        pending.EstimatedCostUsd);

    await DeletePendingActionAsync(pending.Id);
    await MaybeUpdateSummaryAsync(pending.ChatId, cancellationToken);
}

// ======================================================
// TWODAY STUDIO PERSONA
// ======================================================

string BuildStudioIdentityPrompt()
{
    return """
    IDENTITY

    You are TwoDay AI, the independent third teammate inside TwoDay Studio.
    TwoDay Studio is a small two-person, mobile-first game studio that is trying to move unusually fast without lying to itself about data.

    YOUR JOB

    - Act like a sharp third teammate, not a yes-man and not a customer-support bot.
    - Help the team make better product, game-design, analytics, distribution, budget and business decisions.
    - Challenge weak assumptions directly but constructively.
    - If both founders are wrong, say so.
    - If an idea is promising, explain why and what evidence would validate it.
    - Prefer decisions that create learning quickly and cheaply.
    - Protect runway. Distinguish what is worth spending money on from what is merely interesting.
    - Do calculations when useful instead of hand-waving.
    - Separate FACT, ASSUMPTION, TARGET and OPINION when those could be confused.
    - Never manufacture analytics, retention benchmarks, investor interest, market data or user feedback.

    STUDIO OPERATING CONTEXT

    - The studio's current strategy is rapid prototyping, publishing, collecting real player data, and iterating from evidence.
    - Mobile is the current focus; PC/Steam is a later-stage direction rather than the immediate production focus.
    - The studio cares heavily about first-session behavior, the first 30-60 seconds, D1/D7 retention, session length, conversion/funnel drop-off, CPI and whether a mechanic earns further production.
    - Do not recommend building months of content before the core loop proves itself unless there is a strong reason.
    - Distribution can span app stores, web/playable platforms and the studio's own channels; judge platform fit instead of assuming one game should perform equally everywhere.
    - A major long-term working target is to arrive at ChinaJoy 2027 with a portfolio around 12 published IPs, cross-platform proof, and several titles with credible retention evidence. Treat that as a target, never as an achieved fact.
    - In the near term, shipping and collecting trustworthy player data matters more than polishing unpublished games forever.
    - The team may set aggressive D1/D7 retention goals. Treat them as internal targets, not universal industry benchmarks; compare against genre/platform benchmarks only after approved research.
    - Networking and investment stories should be evidence-led. Prefer real traction, learning velocity and a repeatable production process over vanity metrics or inflated valuation talk.
    - The studio is budget-sensitive. Suggest cheap validation before expensive scaling whenever possible.

    GAME DESIGN LENS

    When discussing a game, naturally think through:
    hook -> onboarding/FTUE -> core loop -> short-term reward -> progression/meta -> difficulty pacing -> replay reason -> retention risk -> platform fit -> measurable experiment.

    Do not force this framework into every answer. Use it only when it helps.

    COMMUNICATION STYLE

    - Talk like a real teammate in Telegram.
    - Usually be concise and practical.
    - Turkish when the team speaks Turkish; English when they speak English; mixed language is fine.
    - Light humor is okay, but do not turn every answer into a joke.
    - Avoid corporate filler and motivational fluff.
    - Do not introduce yourself repeatedly and do not say "as an AI".
    - Ask a clarifying question only when the missing information materially changes the decision.

    CONTEXT / AMBIGUITY RULE

    - Interpret ambiguous words using the recent Telegram conversation and long-term studio memory BEFORE using their common public meaning.
    - Preserve meanings already established in conversation.
    - In this bot, Luna, Terra and Sol may refer to OpenAI model tiers. If the conversation establishes that meaning, do not silently reinterpret them as cryptocurrency, astronomy, companies or unrelated products.
    - If two meanings remain genuinely plausible, state the interpretation briefly or ask one short clarification instead of confidently researching the wrong thing.

    TOOL / COST RULE

    - Normal reasoning uses the local conversation context and the default model.
    - Never pretend you searched the web when you did not.
    - Current/fresh facts should use the explicit research flow.
    - Expensive/tool-using actions must go through the bot's preflight confirmation system before execution.
    """;
}

// ======================================================
// NORMAL RESPONSE
// ======================================================

async Task<string> GenerateNormalResponseAsync(
    long chatId,
    long userId,
    BinaryData? image,
    CancellationToken cancellationToken)
{
    string context = await BuildContextAsync(chatId);

    string prompt = $$"""
    {{BuildStudioIdentityPrompt()}}

    IMAGE RULES

    If an image is attached:
    - Inspect it carefully.
    - Read visible UI, charts, analytics, numbers and text.
    - Use visible evidence and do not invent unreadable details.
    - For analytics, identify patterns and practical next experiments.
    - For game art/UI, give production-relevant feedback.
    - If text is too small to read confidently, say so.

    LOCAL MEMORY

    {{context}}

    Respond to the latest discussion. If the user is asking for fresh/current information that cannot be trusted from model knowledge alone, do not fabricate it; tell them a web research action is needed.
    """;

    int estimatedInput = EstimateTokensFromText(prompt) + (image != null ? LOW_VISION_TOKEN_RESERVE : 0);
    const int estimatedOutput = 500;

    var options = new CreateResponseOptions { Model = MODEL };
    List<ResponseContentPart> parts =
    [
        ResponseContentPart.CreateInputTextPart(prompt)
    ];

    if (image != null)
    {
        parts.Add(ResponseContentPart.CreateInputImagePart(
            image,
            ResponseImageDetailLevel.Low));
    }

    options.InputItems.Add(ResponseItem.CreateUserMessageItem(parts));

    ResponseResult response = await ai.CreateResponseAsync(options, cancellationToken);

    decimal cost = CalculateModelCost(estimatedInput, estimatedOutput);
    await RecordCostEstimateAsync(
        chatId,
        userId,
        image != null ? "normal_vision" : "normal_chat",
        estimatedInput,
        estimatedOutput,
        0,
        cost);

    return response.GetOutputText();
}

// ======================================================
// RESEARCH RESPONSE — ONLY CALLED AFTER CONFIRMATION
// ======================================================

async Task<string> GenerateResearchResponseAsync(
    long chatId,
    string originalRequest,
    BinaryData? image,
    CancellationToken cancellationToken)
{
    string context = await BuildContextAsync(chatId);

    string prompt = $$"""
    {{BuildStudioIdentityPrompt()}}

    The initiating human explicitly approved this web research request.

    ORIGINAL REQUEST
    {{originalRequest}}

    LOCAL TEAM CONTEXT
    {{context}}

    RESEARCH RULES

    - Use web search now.
    - Interpret the request through the LOCAL TEAM CONTEXT first.
    - Do not silently change the meaning of established terms.
    - Prefer primary/official sources when available.
    - Cross-check claims that would materially change a decision.
    - Separate current facts from inference.
    - Never fabricate sources or numbers.
    - Focus on the decision TwoDay Studio is actually trying to make.
    - If the studio's assumption is wrong, say so clearly.
    - Keep the answer compact enough for Telegram while preserving useful evidence.

    If an image is attached, combine visible image evidence with web evidence. Do not invent unreadable image details.
    """;

    var options = new CreateResponseOptions { Model = MODEL };
    options.Tools.Add(ResponseTool.CreateWebSearchTool());

    List<ResponseContentPart> parts =
    [
        ResponseContentPart.CreateInputTextPart(prompt)
    ];

    if (image != null)
    {
        parts.Add(ResponseContentPart.CreateInputImagePart(
            image,
            ResponseImageDetailLevel.Low));
    }

    options.InputItems.Add(ResponseItem.CreateUserMessageItem(parts));
    ResponseResult response = await ai.CreateResponseAsync(options, cancellationToken);
    return response.GetOutputText();
}

// ======================================================
// SHOULD AI JOIN THE CHAT?
// ======================================================

async Task<bool> ShouldAiReplyAsync(
    long chatId,
    long userId,
    CancellationToken cancellationToken)
{
    string context = await BuildContextAsync(chatId);

    string prompt = $$"""
    You decide whether TwoDay AI, the independent third teammate of a two-person game studio, should join the Telegram conversation right now.

    Say YES only if the AI can add meaningful value now.

    YES:
    - genuine question or decision
    - game design/product discussion
    - analytics interpretation
    - budget/calculation/business decision
    - meaningful factual/logical mistake
    - strategic warning or strong alternative

    NO:
    - greetings
    - jokes/small talk
    - acknowledgement
    - humans are talking naturally and AI would only add noise

    Do not be overeager.
    Return exactly YES or NO.

    Conversation:
    {{context}}
    """;

    int estimatedInput = EstimateTokensFromText(prompt);
    const int estimatedOutput = 10;

    var options = new CreateResponseOptions { Model = MODEL };
    options.InputItems.Add(ResponseItem.CreateUserMessageItem(prompt));

    ResponseResult response = await ai.CreateResponseAsync(options, cancellationToken);

    await RecordCostEstimateAsync(
        chatId,
        userId,
        "reply_decision",
        estimatedInput,
        estimatedOutput,
        0,
        CalculateModelCost(estimatedInput, estimatedOutput));

    return response.GetOutputText().Trim().StartsWith("YES", StringComparison.OrdinalIgnoreCase);
}

// ======================================================
// RESEARCH PREFLIGHT ESTIMATE
// ======================================================

async Task<ResearchCostEstimate> EstimateResearchCostAsync(
    long chatId,
    string request,
    bool hasImage)
{
    string context = await BuildContextAsync(chatId);

    int estimatedInput = EstimateTokensFromText(context + "\n" + request);
    estimatedInput += 1000; // prompt overhead
    estimatedInput += RESEARCH_SEARCH_CONTENT_TOKEN_RESERVE;

    if (hasImage)
        estimatedInput += LOW_VISION_TOKEN_RESERVE;

    estimatedInput = (int)Math.Ceiling(estimatedInput * 1.20); // safety reserve
    int estimatedOutput = RESEARCH_OUTPUT_TOKEN_RESERVE;
    int webCalls = RESEARCH_WEB_CALL_RESERVE;

    decimal modelCost = CalculateModelCost(estimatedInput, estimatedOutput);
    decimal webCost = webCalls * WEB_SEARCH_PER_CALL;

    return new ResearchCostEstimate(
        estimatedInput,
        estimatedOutput,
        webCalls,
        modelCost,
        webCost,
        modelCost + webCost);
}

int EstimateTokensFromText(string text)
{
    if (string.IsNullOrEmpty(text))
        return 0;

    // Rough multilingual preflight estimate only.
    return Math.Max(1, (int)Math.Ceiling(text.Length / 3.5));
}

decimal CalculateModelCost(int inputTokens, int outputTokens)
{
    decimal inputCost = inputTokens / 1_000_000m * LUNA_INPUT_PER_1M;
    decimal outputCost = outputTokens / 1_000_000m * LUNA_OUTPUT_PER_1M;
    return inputCost + outputCost;
}

// ======================================================
// IMAGE DOWNLOAD / PERSISTENCE
// ======================================================

async Task<ImagePayload> DownloadImageAsync(
    ITelegramBotClient bot,
    Message message,
    long chatId,
    CancellationToken cancellationToken)
{
    string fileId;
    string extension;
    string mediaType;

    if (message.Photo is { Length: > 0 })
    {
        var bestPhoto = message.Photo.OrderByDescending(p => p.FileSize ?? 0).First();
        fileId = bestPhoto.FileId;
        extension = ".jpg";
        mediaType = "image/jpeg";
    }
    else if (message.Document != null &&
             !string.IsNullOrWhiteSpace(message.Document.MimeType) &&
             message.Document.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
    {
        fileId = message.Document.FileId;
        mediaType = NormalizeImageMediaType(message.Document.MimeType);
        extension = GetImageExtension(mediaType, message.Document.FileName);
    }
    else
    {
        throw new Exception("Desteklenen bir görsel bulunamadı.");
    }

    var telegramFile = await bot.GetFile(fileId, cancellationToken);
    if (string.IsNullOrWhiteSpace(telegramFile.FilePath))
        throw new Exception("Telegram FilePath döndürmedi.");

    using MemoryStream memoryStream = new();
    await bot.DownloadFile(telegramFile.FilePath, memoryStream, cancellationToken);
    byte[] bytes = memoryStream.ToArray();

    if (bytes.Length == 0)
        throw new Exception("Telegram'dan boş görsel geldi.");

    string? localPath = await TryPersistImageAsync(
        bytes,
        extension,
        chatId,
        message.MessageId,
        cancellationToken);

    BinaryData binaryData = BinaryData.FromBytes(bytes, mediaType);

    Console.WriteLine(
        $"IMAGE > {bytes.Length / 1024.0:F1} KB | {mediaType} | " +
        (localPath ?? "not saved; vision still available"));

    return new ImagePayload(binaryData, localPath, mediaType);
}

async Task<string?> TryPersistImageAsync(
    byte[] bytes,
    string extension,
    long chatId,
    int messageId,
    CancellationToken cancellationToken)
{
    string filename = $"{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{messageId}{extension}";

    // 1) persistent DATA_DIR
    try
    {
        string chatImageDirectory = Path.Combine(imagesDirectory, chatId.ToString());
        Directory.CreateDirectory(chatImageDirectory);
        string path = Path.Combine(chatImageDirectory, filename);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return path;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"IMAGE SAVE WARNING (persistent) > {ex.Message}");
    }

    // 2) process temp fallback so a pending research can still reuse it during this process
    try
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "twoday-ai-images", chatId.ToString());
        Directory.CreateDirectory(tempDir);
        string tempPath = Path.Combine(tempDir, filename);
        await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
        Console.WriteLine($"IMAGE > temp fallback: {tempPath}");
        return tempPath;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"IMAGE SAVE WARNING (temp) > {ex.Message}");
        return null;
    }
}

async Task<BinaryData?> LoadImageBinaryDataAsync(string path)
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"IMAGE > pending image missing: {path}");
        return null;
    }

    byte[] bytes = await File.ReadAllBytesAsync(path);
    string extension = Path.GetExtension(path).ToLowerInvariant();
    string mediaType = extension switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg"
    };

    return BinaryData.FromBytes(bytes, mediaType);
}

string NormalizeImageMediaType(string mediaType)
{
    string normalized = mediaType.Trim().ToLowerInvariant();
    return normalized switch
    {
        "image/jpg" => "image/jpeg",
        "image/jpeg" => "image/jpeg",
        "image/png" => "image/png",
        "image/webp" => "image/webp",
        "image/gif" => "image/gif",
        _ => normalized
    };
}

string GetImageExtension(string mimeType, string? originalFilename)
{
    if (!string.IsNullOrWhiteSpace(originalFilename))
    {
        string ext = Path.GetExtension(originalFilename);
        if (!string.IsNullOrWhiteSpace(ext))
            return ext;
    }

    return mimeType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".jpg"
    };
}

// ======================================================
// STARTUP GREETING / FEATURE CHANGELOG
// ======================================================

async Task<List<long>> GetStartupChatIdsAsync()
{
    List<long> result = [];

    if (!string.IsNullOrWhiteSpace(startupChatIdsEnv))
    {
        foreach (string part in startupChatIdsEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(part, out long id))
                result.Add(id);
        }

        return result.Distinct().ToList();
    }

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        SELECT ChatId
        FROM Messages
        GROUP BY ChatId
        ORDER BY MAX(Id) DESC
        LIMIT 5;
        """;

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        result.Add(reader.GetInt64(0));

    return result;
}

async Task EnsureStartupGreetingAsync(
    ITelegramBotClient bot,
    long chatId,
    CancellationToken cancellationToken)
{
    if (startupGreetingSentThisProcess.ContainsKey(chatId))
        return;

    await startupGreetingLock.WaitAsync(cancellationToken);
    try
    {
        if (startupGreetingSentThisProcess.ContainsKey(chatId))
            return;

        ReleaseState? previous = await GetReleaseStateAsync(chatId);
        HashSet<string> oldFeatures = previous?.Features?.ToHashSet(StringComparer.Ordinal) ?? [];
        List<string> newFeatures = BUILD_FEATURES
            .Where(f => !oldFeatures.Contains(f))
            .ToList();

        StringBuilder text = new();
        text.AppendLine($"🦖 TwoDay AI v{BOT_VERSION} online.");

        if (newFeatures.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Bu sürümde yeni kazandıklarım:");
            foreach (string feature in newFeatures)
                text.AppendLine($"• {feature}");

            text.AppendLine();
            text.Append("Hafıza bağlı, vision LOW, web araştırması onay korumalı. Hazırım.");
        }
        else
        {
            text.Append("Hafıza yerinde; yeniden nöbetteyim. Devam edebiliriz.");
        }

        await bot.SendMessage(chatId, text.ToString(), cancellationToken: cancellationToken);
        await SaveReleaseStateAsync(chatId, BOT_VERSION, BUILD_FEATURES);
        startupGreetingSentThisProcess.TryAdd(chatId, 0);
    }
    catch (Exception ex)
    {
        // Do not block the bot just because startup announcement failed.
        Console.WriteLine($"STARTUP GREETING WARNING chat={chatId} > {ex.Message}");
        startupGreetingSentThisProcess.TryAdd(chatId, 0);
    }
    finally
    {
        startupGreetingLock.Release();
    }
}

async Task<ReleaseState?> GetReleaseStateAsync(long chatId)
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        SELECT LastVersion, FeaturesJson
        FROM ReleaseAnnouncements
        WHERE ChatId = $chatId;
        """;
    command.Parameters.AddWithValue("$chatId", chatId);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return null;

    string version = reader.GetString(0);
    string json = reader.GetString(1);
    string[] features;

    try
    {
        features = JsonSerializer.Deserialize<string[]>(json) ?? [];
    }
    catch
    {
        features = [];
    }

    return new ReleaseState(version, features);
}

async Task SaveReleaseStateAsync(long chatId, string version, IEnumerable<string> features)
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        INSERT INTO ReleaseAnnouncements(ChatId, LastVersion, FeaturesJson, UpdatedAt)
        VALUES($chatId, $version, $features, $updatedAt)
        ON CONFLICT(ChatId) DO UPDATE SET
            LastVersion = excluded.LastVersion,
            FeaturesJson = excluded.FeaturesJson,
            UpdatedAt = excluded.UpdatedAt;
        """;
    command.Parameters.AddWithValue("$chatId", chatId);
    command.Parameters.AddWithValue("$version", version);
    command.Parameters.AddWithValue("$features", JsonSerializer.Serialize(features));
    command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
    await command.ExecuteNonQueryAsync();
}

// ======================================================
// CONTEXT / MEMORY
// ======================================================

async Task<string> BuildContextAsync(long chatId)
{
    string summary = await GetSummaryAsync(chatId) ?? "Henüz uzun dönem özeti yok.";
    List<MemoryMessage> messages = await GetRecentMessagesAsync(chatId, RECENT_MESSAGE_COUNT);

    StringBuilder builder = new();
    builder.AppendLine("LONG-TERM CHAT SUMMARY:");
    builder.AppendLine(summary);
    builder.AppendLine();
    builder.AppendLine("RECENT MESSAGES:");

    foreach (var message in messages)
        builder.AppendLine($"{message.Name}: {message.Content}");

    return builder.ToString();
}

async Task MaybeUpdateSummaryAsync(long chatId, CancellationToken cancellationToken)
{
    int humanMessageCount = await GetHumanMessageCountSinceSummaryAsync(chatId);
    if (humanMessageCount < SUMMARIZE_EVERY_HUMAN_MESSAGES)
        return;

    Console.WriteLine("AI > uzun dönem hafıza güncelleniyor...");

    string oldSummary = await GetSummaryAsync(chatId) ?? "";
    List<MemoryMessage> newMessages = await GetMessagesSinceLastSummaryAsync(chatId);
    StringBuilder conversation = new();

    foreach (var message in newMessages)
        conversation.AppendLine($"{message.Name}: {message.Content}");

    string prompt = $$"""
    Update the long-term memory summary for TwoDay Studio's Telegram group.

    Preserve only durable information likely to matter later:
    - decisions and goals
    - games/projects and design choices
    - useful analytics/metrics and experiments
    - budgets/plans
    - unresolved questions or meaningful disagreement
    - distribution/business/networking decisions
    - important findings from image analysis

    Exclude greetings, jokes, repetitive chatter and local file paths.
    Distinguish targets from achieved results.
    Never invent anything.
    Keep it compact.

    OLD SUMMARY:
    {{oldSummary}}

    NEW CONVERSATION:
    {{conversation}}

    Return only the updated summary.
    """;

    int estimatedInput = EstimateTokensFromText(prompt);
    const int estimatedOutput = 800;

    var options = new CreateResponseOptions { Model = MODEL };
    options.InputItems.Add(ResponseItem.CreateUserMessageItem(prompt));
    ResponseResult response = await ai.CreateResponseAsync(options, cancellationToken);

    string newSummary = response.GetOutputText().Trim();
    await SaveSummaryAsync(chatId, newSummary);

    await RecordCostEstimateAsync(
        chatId,
        0,
        "memory_summary",
        estimatedInput,
        estimatedOutput,
        0,
        CalculateModelCost(estimatedInput, estimatedOutput));

    Console.WriteLine("AI > hafıza güncellendi.");
}

// ======================================================
// SEND / STORE ASSISTANT OUTPUT
// ======================================================

async Task SendAndStoreAssistantAnswerAsync(
    ITelegramBotClient bot,
    long chatId,
    string answer,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(answer))
        return;

    answer = answer.Trim();

    // Telegram message text limit is ~4096 chars; stay slightly below it.
    if (answer.Length > 4000)
        answer = answer[..4000] + "\n\n[Yanıt Telegram sınırı nedeniyle kısaltıldı.]";

    await bot.SendMessage(chatId, answer, cancellationToken: cancellationToken);
    await SaveMessageAsync(chatId, 0, "TwoDay AI", "assistant", answer);
    Console.WriteLine($"AI > {answer}");
}

// ======================================================
// BUDGET
// ======================================================

bool CanManageBudget(long userId)
{
    // If no admin list is configured, preserve simple two-person-group behavior.
    return adminUserIds.Count == 0 || adminUserIds.Contains(userId);
}

async Task SendBudgetStatusAsync(
    ITelegramBotClient bot,
    long chatId,
    CancellationToken cancellationToken)
{
    decimal spend = await GetTodayEstimatedSpendAsync(chatId);
    bool unlocked = await IsBudgetUnlockedAsync(chatId);

    await bot.SendMessage(
        chatId,
        $"💰 Bugünkü tahmini API kullanımı: ${spend:F4}\n" +
        $"Soft limit: ${DAILY_SOFT_LIMIT_USD:F2}\n" +
        $"Hard limit: ${DAILY_HARD_LIMIT_USD:F2}\n" +
        $"Durum: {(unlocked ? "🔓 hard limit bugün açık" : "🔒 hard limit aktif")}\n\n" +
        "Not: Bu botun preflight/usage tahminidir; OpenAI faturasıyla birebir aynı olmak zorunda değildir.",
        cancellationToken: cancellationToken);
}

string GetBudgetDateKey()
{
    return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, studioTimeZone)
        .ToString("yyyy-MM-dd");
}

(DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetTodayUtcRange()
{
    DateTimeOffset localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, studioTimeZone);
    DateTime localStart = DateTime.SpecifyKind(localNow.Date, DateTimeKind.Unspecified);
    DateTime localEnd = localStart.AddDays(1);
    DateTime startUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, studioTimeZone);
    DateTime endUtc = TimeZoneInfo.ConvertTimeToUtc(localEnd, studioTimeZone);
    return (new DateTimeOffset(startUtc), new DateTimeOffset(endUtc));
}

async Task<decimal> GetTodayEstimatedSpendAsync(long chatId)
{
    var range = GetTodayUtcRange();

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        SELECT COALESCE(SUM(EstimatedCostUsd), 0)
        FROM CostLedger
        WHERE ChatId = $chatId
          AND CreatedAt >= $start
          AND CreatedAt < $end;
        """;
    command.Parameters.AddWithValue("$chatId", chatId);
    command.Parameters.AddWithValue("$start", range.StartUtc.ToString("O"));
    command.Parameters.AddWithValue("$end", range.EndUtc.ToString("O"));

    object? result = await command.ExecuteScalarAsync();
    return Convert.ToDecimal(result);
}

async Task<bool> IsBudgetUnlockedAsync(long chatId)
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        SELECT IsUnlocked
        FROM BudgetUnlocks
        WHERE ChatId = $chatId AND BudgetDate = $date;
        """;
    command.Parameters.AddWithValue("$chatId", chatId);
    command.Parameters.AddWithValue("$date", GetBudgetDateKey());

    object? result = await command.ExecuteScalarAsync();
    return result != null && result != DBNull.Value && Convert.ToInt32(result) == 1;
}

async Task SetBudgetUnlockedAsync(long chatId, bool unlocked)
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        INSERT INTO BudgetUnlocks(ChatId, BudgetDate, IsUnlocked)
        VALUES($chatId, $date, $value)
        ON CONFLICT(ChatId, BudgetDate)
        DO UPDATE SET IsUnlocked = excluded.IsUnlocked;
        """;
    command.Parameters.AddWithValue("$chatId", chatId);
    command.Parameters.AddWithValue("$date", GetBudgetDateKey());
    command.Parameters.AddWithValue("$value", unlocked ? 1 : 0);
    await command.ExecuteNonQueryAsync();
}

// ======================================================
// DATABASE INIT
// ======================================================

async Task InitializeDatabaseAsync()
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        CREATE TABLE IF NOT EXISTS Messages
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ChatId INTEGER NOT NULL,
            UserId INTEGER NOT NULL,
            UserName TEXT NOT NULL,
            Role TEXT NOT NULL,
            Content TEXT NOT NULL,
            CreatedAt TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_Messages_ChatId
        ON Messages(ChatId);

        CREATE TABLE IF NOT EXISTS Summaries
        (
            ChatId INTEGER PRIMARY KEY,
            Summary TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            LastMessageId INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS PendingActions
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ChatId INTEGER NOT NULL,
            UserId INTEGER NOT NULL,
            ActionType TEXT NOT NULL,
            OriginalRequest TEXT NOT NULL,
            ImagePath TEXT NULL,
            EstimatedInputTokens INTEGER NOT NULL,
            EstimatedOutputTokens INTEGER NOT NULL,
            EstimatedWebRuns INTEGER NOT NULL,
            EstimatedCostUsd REAL NOT NULL,
            CreatedAt TEXT NOT NULL,
            ExpiresAt TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_PendingActions_User
        ON PendingActions(ChatId, UserId);

        CREATE TABLE IF NOT EXISTS CostLedger
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ChatId INTEGER NOT NULL,
            UserId INTEGER NOT NULL,
            ActionType TEXT NOT NULL,
            EstimatedInputTokens INTEGER NOT NULL,
            EstimatedOutputTokens INTEGER NOT NULL,
            EstimatedWebRuns INTEGER NOT NULL,
            EstimatedCostUsd REAL NOT NULL,
            CreatedAt TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_CostLedger_ChatDate
        ON CostLedger(ChatId, CreatedAt);

        CREATE TABLE IF NOT EXISTS BudgetUnlocks
        (
            ChatId INTEGER NOT NULL,
            BudgetDate TEXT NOT NULL,
            IsUnlocked INTEGER NOT NULL,
            PRIMARY KEY(ChatId, BudgetDate)
        );

        CREATE TABLE IF NOT EXISTS ReleaseAnnouncements
        (
            ChatId INTEGER PRIMARY KEY,
            LastVersion TEXT NOT NULL,
            FeaturesJson TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        """;

    await command.ExecuteNonQueryAsync();
}

// ======================================================
// PENDING ACTIONS
// ======================================================

async Task CreatePendingActionAsync(
    long chatId,
    long userId,
    string actionType,
    string originalRequest,
    string? imagePath,
    int estimatedInputTokens,
    int estimatedOutputTokens,
    int estimatedWebCalls,
    decimal estimatedCostUsd)
{
    await DeletePendingActionsForUserAsync(chatId, userId);

    DateTimeOffset now = DateTimeOffset.UtcNow;
    DateTimeOffset expires = now.AddMinutes(PENDING_ACTION_EXPIRY_MINUTES);

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        INSERT INTO PendingActions
        (
            ChatId, UserId, ActionType, OriginalRequest, ImagePath,
            EstimatedInputTokens, EstimatedOutputTokens, EstimatedWebRuns,
            EstimatedCostUsd, CreatedAt, ExpiresAt
        )
        VALUES
        (
            $chatId, $userId, $actionType, $originalRequest, $imagePath,
            $input, $output, $web, $cost, $createdAt, $expiresAt
        );
        """;
    command.Parameters.AddWithValue("$chatId", chatId);
    command.Parameters.AddWithValue("$userId", userId);
    command.Parameters.AddWithValue("$actionType", actionType);
    command.Parameters.AddWithValue("$originalRequest", originalRequest);
    command.Parameters.AddWithValue("$imagePath", (object?)imagePath ?? DBNull.Value);
    command.Parameters.AddWithValue("$input", estimatedInputTokens);
    command.Parameters.AddWithValue("$output", estimatedOutputTokens);
    command.Parameters.AddWithValue("$web", estimatedWebCalls);
    command.Parameters.AddWithValue("$cost", estimatedCostUsd);
    command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
    command.Parameters.AddWithValue("$expiresAt", expires.ToString("O"));
    await command.ExecuteNonQueryAsync();
}

async Task<PendingAction?> GetPendingActionAsync(long chatId, long userId)
{
    await CleanupExpiredPendingActionsAsync();

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        SELECT
            Id, ChatId, UserId, ActionType, OriginalRequest, ImagePath,
            EstimatedInputTokens, EstimatedOutputTokens, EstimatedWebRuns,
            EstimatedCostUsd, CreatedAt, ExpiresAt
        FROM PendingActions
        WHERE ChatId = $chatId AND UserId = $userId
        ORDER BY Id DESC
        LIMIT 1;
        """;
    command.Parameters.AddWithValue("$chatId", chatId);
    command.Parameters.AddWithValue("$userId", userId);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return null;

    return new PendingAction(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetInt64(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetInt32(6),
        reader.GetInt32(7),
        reader.GetInt32(8),
        Convert.ToDecimal(reader.GetDouble(9)),
        reader.GetString(10),
        reader.GetString(11));
}

async Task DeletePendingActionAsync(long id)
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "DELETE FROM PendingActions WHERE Id = $id;";
    command.Parameters.AddWithValue("$id", id);
    await command.ExecuteNonQueryAsync();
}

async Task DeletePendingActionsForUserAsync(long chatId, long userId)
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "DELETE FROM PendingActions WHERE ChatId = $chatId AND UserId = $userId;";
    command.Parameters.AddWithValue("$chatId", chatId);
    command.Parameters.AddWithValue("$userId", userId);
    await command.ExecuteNonQueryAsync();
}

async Task CleanupExpiredPendingActionsAsync()
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "DELETE FROM PendingActions WHERE ExpiresAt < $now;";
    command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
    await command.ExecuteNonQueryAsync();
}

// ======================================================
// COST LEDGER
// ======================================================

async Task RecordCostEstimateAsync(
    long chatId,
    long userId,
    string actionType,
    int inputTokens,
    int outputTokens,
    int webCalls,
    decimal estimatedCostUsd)
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        INSERT INTO CostLedger
        (
            ChatId, UserId, ActionType,
            EstimatedInputTokens, EstimatedOutputTokens, EstimatedWebRuns,
            EstimatedCostUsd, CreatedAt
        )
        VALUES
        (
            $chatId, $userId, $actionType,
            $input, $output, $web, $cost, $createdAt
        );
        """;
    command.Parameters.AddWithValue("$chatId", chatId);
    command.Parameters.AddWithValue("$userId", userId);
    command.Parameters.AddWithValue("$actionType", actionType);
    command.Parameters.AddWithValue("$input", inputTokens);
    command.Parameters.AddWithValue("$output", outputTokens);
    command.Parameters.AddWithValue("$web", webCalls);
    command.Parameters.AddWithValue("$cost", estimatedCostUsd);
    command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
    await command.ExecuteNonQueryAsync();
}

// ======================================================
// MESSAGE MEMORY DB
// ======================================================

async Task SaveMessageAsync(
    long chatId,
    long userId,
    string userName,
    string role,
    string content)
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        INSERT INTO Messages(ChatId, UserId, UserName, Role, Content, CreatedAt)
        VALUES($chatId, $userId, $userName, $role, $content, $createdAt);
        """;
    command.Parameters.AddWithValue("$chatId", chatId);
    command.Parameters.AddWithValue("$userId", userId);
    command.Parameters.AddWithValue("$userName", userName);
    command.Parameters.AddWithValue("$role", role);
    command.Parameters.AddWithValue("$content", content);
    command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
    await command.ExecuteNonQueryAsync();
}

async Task<List<MemoryMessage>> GetRecentMessagesAsync(long chatId, int count)
{
    List<MemoryMessage> messages = [];

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        SELECT Id, UserName, Role, Content, CreatedAt
        FROM Messages
        WHERE ChatId = $chatId
        ORDER BY Id DESC
        LIMIT $count;
        """;
    command.Parameters.AddWithValue("$chatId", chatId);
    command.Parameters.AddWithValue("$count", count);

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        messages.Add(new MemoryMessage(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4)));
    }

    messages.Reverse();
    return messages;
}

async Task<string?> GetSummaryAsync(long chatId)
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT Summary FROM Summaries WHERE ChatId = $chatId;";
    command.Parameters.AddWithValue("$chatId", chatId);
    object? result = await command.ExecuteScalarAsync();
    return result == null || result == DBNull.Value ? null : result.ToString();
}

async Task SaveSummaryAsync(long chatId, string summary)
{
    long lastMessageId = await GetLastMessageIdAsync(chatId);

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        INSERT INTO Summaries(ChatId, Summary, UpdatedAt, LastMessageId)
        VALUES($chatId, $summary, $updatedAt, $lastMessageId)
        ON CONFLICT(ChatId)
        DO UPDATE SET
            Summary = excluded.Summary,
            UpdatedAt = excluded.UpdatedAt,
            LastMessageId = excluded.LastMessageId;
        """;
    command.Parameters.AddWithValue("$chatId", chatId);
    command.Parameters.AddWithValue("$summary", summary);
    command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
    command.Parameters.AddWithValue("$lastMessageId", lastMessageId);
    await command.ExecuteNonQueryAsync();
}

async Task<int> GetHumanMessageCountSinceSummaryAsync(long chatId)
{
    long lastSummaryMessageId = await GetLastSummaryMessageIdAsync(chatId);

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        SELECT COUNT(*)
        FROM Messages
        WHERE ChatId = $chatId
          AND Id > $lastId
          AND Role = 'user';
        """;
    command.Parameters.AddWithValue("$chatId", chatId);
    command.Parameters.AddWithValue("$lastId", lastSummaryMessageId);
    object? result = await command.ExecuteScalarAsync();
    return Convert.ToInt32(result);
}

async Task<List<MemoryMessage>> GetMessagesSinceLastSummaryAsync(long chatId)
{
    long lastSummaryMessageId = await GetLastSummaryMessageIdAsync(chatId);
    List<MemoryMessage> messages = [];

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        SELECT Id, UserName, Role, Content, CreatedAt
        FROM Messages
        WHERE ChatId = $chatId AND Id > $lastId
        ORDER BY Id ASC;
        """;
    command.Parameters.AddWithValue("$chatId", chatId);
    command.Parameters.AddWithValue("$lastId", lastSummaryMessageId);

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        messages.Add(new MemoryMessage(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4)));
    }

    return messages;
}

async Task<long> GetLastSummaryMessageIdAsync(long chatId)
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT LastMessageId FROM Summaries WHERE ChatId = $chatId;";
    command.Parameters.AddWithValue("$chatId", chatId);
    object? result = await command.ExecuteScalarAsync();
    return result == null || result == DBNull.Value ? 0 : Convert.ToInt64(result);
}

async Task<long> GetLastMessageIdAsync(long chatId)
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT COALESCE(MAX(Id), 0) FROM Messages WHERE ChatId = $chatId;";
    command.Parameters.AddWithValue("$chatId", chatId);
    object? result = await command.ExecuteScalarAsync();
    return Convert.ToInt64(result);
}

// ======================================================
// COMMAND / TEXT HELPERS
// ======================================================

bool IsResearchRequest(string text)
{
    if (string.IsNullOrWhiteSpace(text))
        return false;

    string normalized = text.Trim().ToLowerInvariant();

    string[] triggers =
    {
        "araştır",
        "arastir",
        "research ",
        "research:",
        "/research",
        "/web",
        "webden bak",
        "web'den bak",
        "internetten bak",
        "internetten araştır",
        "internetten arastir"
    };

    return triggers.Any(trigger => normalized.Contains(trigger));
}

bool IsConfirmationText(string text)
{
    string n = NormalizeCommandText(text);
    return n is "evet" or "yes" or "onay" or "onayla" or "devam" or "devam et" or "ok" or "okay";
}

bool IsCancellationText(string text)
{
    string n = NormalizeCommandText(text);
    return n is "hayır" or "hayir" or "no" or "iptal" or "iptal et" or "vazgeç" or "vazgec";
}

string NormalizeCommandText(string text)
{
    return text.Trim().ToLowerInvariant().TrimEnd('.', '!', '?', ',', ';', ':');
}

bool IsBotMentioned(string text, string username)
{
    return !string.IsNullOrWhiteSpace(text) &&
           !string.IsNullOrWhiteSpace(username) &&
           text.Contains($"@{username}", StringComparison.OrdinalIgnoreCase);
}

string GetSenderName(User? user)
{
    if (user == null)
        return "Unknown";

    string fullName = $"{user.FirstName} {user.LastName}".Trim();
    if (!string.IsNullOrWhiteSpace(fullName))
        return fullName;

    if (!string.IsNullOrWhiteSpace(user.Username))
        return user.Username;

    return $"User-{user.Id}";
}

HashSet<long> ParseLongSet(string? raw)
{
    HashSet<long> result = [];
    if (string.IsNullOrWhiteSpace(raw))
        return result;

    foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (long.TryParse(part, out long value))
            result.Add(value);
    }

    return result;
}

Task HandleErrorAsync(
    ITelegramBotClient bot,
    Exception exception,
    CancellationToken cancellationToken)
{
    Console.WriteLine();
    Console.WriteLine("TELEGRAM ERROR:");
    Console.WriteLine(exception);
    return Task.CompletedTask;
}

// ======================================================
// DATA MODELS
// ======================================================

record MemoryMessage(
    long Id,
    string Name,
    string Role,
    string Content,
    string CreatedAt);

record ImagePayload(
    BinaryData Data,
    string? LocalPath,
    string MediaType);

record ResearchCostEstimate(
    int InputTokens,
    int OutputTokens,
    int WebCalls,
    decimal ModelCostUsd,
    decimal WebCostUsd,
    decimal TotalCostUsd);

record PendingAction(
    long Id,
    long ChatId,
    long UserId,
    string ActionType,
    string OriginalRequest,
    string? ImagePath,
    int EstimatedInputTokens,
    int EstimatedOutputTokens,
    int EstimatedWebCalls,
    decimal EstimatedCostUsd,
    string CreatedAt,
    string ExpiresAt);

record ReleaseState(
    string Version,
    string[] Features);
