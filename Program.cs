#pragma warning disable OPENAI001

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenAI.Responses;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

// ======================================================
// CONFIG
// ======================================================

const string MODEL = "gpt-5.6-luna";

const int RECENT_MESSAGE_COUNT = 30;
const int SUMMARIZE_EVERY_HUMAN_MESSAGES = 60;

const string DATABASE_FILE = "twoday-ai.db";

// ======================================================
// ENVIRONMENT VARIABLES
// ======================================================

string telegramToken =
    Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
    ?? throw new Exception("TELEGRAM_BOT_TOKEN bulunamadı.");

string openAiKey =
    Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? throw new Exception("OPENAI_API_KEY bulunamadı.");

// ======================================================
// CLIENTS
// ======================================================

var telegram =
    new TelegramBotClient(telegramToken);

var ai =
    new ResponsesClient(openAiKey);

var chatLocks =
    new ConcurrentDictionary<long, SemaphoreSlim>();

CancellationTokenSource cts = new();

// ======================================================
// DATA DIRECTORY
// ======================================================

string dataDirectory =
    Environment.GetEnvironmentVariable("DATA_DIR")
    ?? AppContext.BaseDirectory;

Directory.CreateDirectory(dataDirectory);

// ======================================================
// DATABASE
// ======================================================

string databaseFile =
    Path.Combine(
        dataDirectory,
        DATABASE_FILE
    );

string? seedDatabaseFile =
    Environment.GetEnvironmentVariable(
        "SEED_DATABASE_FILE"
    );

if (!File.Exists(databaseFile) &&
    !string.IsNullOrWhiteSpace(seedDatabaseFile) &&
    File.Exists(seedDatabaseFile))
{
    File.Copy(
        seedDatabaseFile,
        databaseFile
    );

    Console.WriteLine(
        $"Eski hafıza {databaseFile} konumuna taşındı."
    );
}

string connectionString =
    $"Data Source={databaseFile}";

// ======================================================
// IMAGE DIRECTORY
// ======================================================

string imagesDirectory =
    Path.Combine(
        dataDirectory,
        "images"
    );

Directory.CreateDirectory(
    imagesDirectory
);

// ======================================================
// SAFE SHUTDOWN
// ======================================================

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    cts.Cancel();
};

// ======================================================
// DATABASE INIT
// ======================================================

await InitializeDatabaseAsync();

// ======================================================
// BOT INFO
// ======================================================

var me =
    await telegram.GetMe(cts.Token);

string botUsername =
    me.Username ?? "";

Console.WriteLine();
Console.WriteLine("====================================");
Console.WriteLine(" TwoDay AI ONLINE");
Console.WriteLine($" @{botUsername}");
Console.WriteLine($" Model: {MODEL}");
Console.WriteLine($" Database: {databaseFile}");
Console.WriteLine($" Images: {imagesDirectory}");
Console.WriteLine(" Vision: LOW");
Console.WriteLine("====================================");
Console.WriteLine();

// ======================================================
// TELEGRAM RECEIVER
// ======================================================

ReceiverOptions receiverOptions = new()
{
    AllowedUpdates =
    [
        UpdateType.Message
    ]
};

telegram.StartReceiving(
    updateHandler: HandleUpdateAsync,
    errorHandler: HandleErrorAsync,
    receiverOptions: receiverOptions,
    cancellationToken: cts.Token
);

Console.WriteLine("Bot mesaj bekliyor.");
Console.WriteLine("Durdurma sinyali bekleniyor.");

try
{
    await Task.Delay(
        Timeout.InfiniteTimeSpan,
        cts.Token
    );
}
catch (OperationCanceledException)
{
    Console.WriteLine(
        "Bot kapatılıyor."
    );
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

    // ==================================================
    // TEXT / CAPTION / IMAGE DETECTION
    // ==================================================

    string text =
        message.Text
        ?? message.Caption
        ?? "";

    bool hasText =
        !string.IsNullOrWhiteSpace(text);

    bool hasPhoto =
        message.Photo is { Length: > 0 };

    bool hasImageDocument =
        message.Document != null &&
        !string.IsNullOrWhiteSpace(
            message.Document.MimeType
        ) &&
        message.Document.MimeType.StartsWith(
            "image/",
            StringComparison.OrdinalIgnoreCase
        );

    bool hasImage =
        hasPhoto ||
        hasImageDocument;

    // Şimdilik text ve görsel dışındakileri pas geç.
    if (!hasText && !hasImage)
        return;

    long chatId =
        message.Chat.Id;

    long senderId =
        message.From?.Id ?? 0;

    string senderName =
        GetSenderName(
            message.From
        );

    // ==================================================
    // IMAGE DOWNLOAD
    // ==================================================

    ImagePayload? image = null;

    if (hasImage)
    {
        try
        {
            image =
                await DownloadImageAsync(
                    bot,
                    message,
                    chatId,
                    cancellationToken
                );

            Console.WriteLine(
                $"[{message.Chat.Title ?? chatId.ToString()}] " +
                $"{senderName}: [GÖRSEL] {text}"
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"IMAGE DOWNLOAD ERROR > {ex}"
            );

            await bot.SendMessage(
                chatId: chatId,
                text:
                    "Görseli Telegram'dan indirirken bir sorun yaşadım.",
                cancellationToken: cancellationToken
            );

            return;
        }
    }
    else
    {
        Console.WriteLine(
            $"[{message.Chat.Title ?? chatId.ToString()}] " +
            $"{senderName}: {text}"
        );
    }

    // ==================================================
    // SAVE USER MESSAGE
    // ==================================================

    string databaseMessage;

    if (image != null)
    {
        databaseMessage =
            "[GÖRSEL]";

        if (!string.IsNullOrWhiteSpace(text))
        {
            databaseMessage +=
                $" Caption: {text}";
        }

        databaseMessage +=
            $" | LocalPath: {image.LocalPath}";
    }
    else
    {
        databaseMessage =
            text;
    }

    await SaveMessageAsync(
        chatId,
        senderId,
        senderName,
        "user",
        databaseMessage
    );

    // ==================================================
    // CHAT LOCK
    // ==================================================

    var chatLock =
        chatLocks.GetOrAdd(
            chatId,
            _ => new SemaphoreSlim(1, 1)
        );

    await chatLock.WaitAsync(
        cancellationToken
    );

    try
    {
        // ==================================================
        // DETERMINE MODE
        // ==================================================

        bool isResearch =
            IsResearchRequest(
                text
            );

        bool directlyMentioned =
            IsBotMentioned(
                text,
                botUsername
            );

        // Görsel gönderildiyse cevap versin.
        bool shouldReply =
            hasImage ||
            isResearch ||
            directlyMentioned;

        // Normal konuşmada karar versin.
        if (!shouldReply)
        {
            shouldReply =
                await ShouldAiReplyAsync(
                    chatId,
                    cancellationToken
                );
        }

        if (!shouldReply)
        {
            Console.WriteLine(
                "AI > sessiz kaldı"
            );

            await MaybeUpdateSummaryAsync(
                chatId,
                cancellationToken
            );

            return;
        }

        await bot.SendChatAction(
            chatId: chatId,
            action: ChatAction.Typing,
            cancellationToken: cancellationToken
        );

        string answer;

        // ==================================================
        // RESEARCH
        // ==================================================

        if (isResearch)
        {
            Console.WriteLine(
                image != null
                    ? "AI > görsel + web araştırması yapıyor..."
                    : "AI > web araştırması yapıyor..."
            );

            answer =
                await GenerateResearchResponseAsync(
                    chatId,
                    image?.Data,
                    cancellationToken
                );
        }

        // ==================================================
        // NORMAL / VISION
        // ==================================================

        else
        {
            Console.WriteLine(
                image != null
                    ? "AI > görseli LOW detail ile inceliyor..."
                    : "AI > cevap oluşturuyor..."
            );

            answer =
                await GenerateNormalResponseAsync(
                    chatId,
                    image?.Data,
                    cancellationToken
                );
        }

        if (string.IsNullOrWhiteSpace(
            answer))
        {
            return;
        }

        answer =
            answer.Trim();

        // Telegram mesaj sınırı
        if (answer.Length > 4000)
        {
            answer =
                answer[..4000] +
                "\n\n[Yanıt Telegram sınırı nedeniyle kısaltıldı.]";
        }

        await bot.SendMessage(
            chatId: chatId,
            text: answer,
            cancellationToken: cancellationToken
        );

        // AI cevabını hafızaya yaz.
        await SaveMessageAsync(
            chatId,
            0,
            "TwoDay AI",
            "assistant",
            answer
        );

        Console.WriteLine(
            $"AI > {answer}"
        );

        await MaybeUpdateSummaryAsync(
            chatId,
            cancellationToken
        );
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"AI ERROR > {ex}"
        );

        try
        {
            await bot.SendMessage(
                chatId: chatId,
                text:
                    "Bir hata yaşadım. Birazdan tekrar deneyelim.",
                cancellationToken: cancellationToken
            );
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
// DOWNLOAD IMAGE
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

    // ==================================================
    // NORMAL TELEGRAM PHOTO
    // ==================================================

    if (message.Photo is { Length: > 0 })
    {
        var bestPhoto =
            message.Photo
                .OrderByDescending(
                    photo =>
                        photo.FileSize ?? 0
                )
                .First();

        fileId =
            bestPhoto.FileId;

        extension =
            ".jpg";

        mediaType =
            "image/jpeg";
    }

    // ==================================================
    // IMAGE AS DOCUMENT
    // ==================================================

    else if (
        message.Document != null &&
        !string.IsNullOrWhiteSpace(
            message.Document.MimeType
        ) &&
        message.Document.MimeType.StartsWith(
            "image/",
            StringComparison.OrdinalIgnoreCase))
    {
        fileId =
            message.Document.FileId;

        mediaType =
            NormalizeImageMediaType(
                message.Document.MimeType
            );

        extension =
            GetImageExtension(
                mediaType,
                message.Document.FileName
            );
    }
    else
    {
        throw new Exception(
            "Desteklenen bir görsel bulunamadı."
        );
    }

    // ==================================================
    // GET TELEGRAM FILE
    // ==================================================

    var telegramFile =
        await bot.GetFile(
            fileId,
            cancellationToken
        );

    if (string.IsNullOrWhiteSpace(
        telegramFile.FilePath))
    {
        throw new Exception(
            "Telegram FilePath döndürmedi."
        );
    }

    // ==================================================
    // DOWNLOAD BYTES
    // ==================================================

    using MemoryStream memoryStream =
        new();

    await bot.DownloadFile(
        telegramFile.FilePath,
        memoryStream,
        cancellationToken
    );

    byte[] bytes =
        memoryStream.ToArray();

    if (bytes.Length == 0)
    {
        throw new Exception(
            "Telegram'dan boş görsel geldi."
        );
    }

    // ==================================================
    // SAVE LOCALLY
    // ==================================================

    string chatImageDirectory =
        Path.Combine(
            imagesDirectory,
            chatId.ToString()
        );

    Directory.CreateDirectory(
        chatImageDirectory
    );

    string filename =
        $"{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_" +
        $"{message.MessageId}" +
        extension;

    string localPath =
        Path.Combine(
            chatImageDirectory,
            filename
        );

    await File.WriteAllBytesAsync(
        localPath,
        bytes,
        cancellationToken
    );

    // ==================================================
    // IMPORTANT:
    // BinaryData MUST have MediaType.
    // ==================================================

    BinaryData binaryData =
        BinaryData.FromBytes(
            bytes,
            mediaType
        );

    Console.WriteLine(
        $"IMAGE > {bytes.Length / 1024.0:F1} KB " +
        $"| {mediaType} | {localPath}"
    );

    return new ImagePayload(
        binaryData,
        localPath,
        mediaType
    );
}

// ======================================================
// NORMALIZE IMAGE MIME TYPE
// ======================================================

string NormalizeImageMediaType(
    string mediaType)
{
    string normalized =
        mediaType
            .Trim()
            .ToLowerInvariant();

    return normalized switch
    {
        "image/jpeg" =>
            "image/jpeg",

        "image/jpg" =>
            "image/jpeg",

        "image/png" =>
            "image/png",

        "image/webp" =>
            "image/webp",

        "image/gif" =>
            "image/gif",

        // Bilinmeyen image/* için güvenli fallback.
        _ =>
            normalized
    };
}

// ======================================================
// IMAGE EXTENSION
// ======================================================

string GetImageExtension(
    string mimeType,
    string? originalFilename)
{
    if (!string.IsNullOrWhiteSpace(
        originalFilename))
    {
        string ext =
            Path.GetExtension(
                originalFilename
            );

        if (!string.IsNullOrWhiteSpace(
            ext))
        {
            return ext;
        }
    }

    return mimeType
        .ToLowerInvariant()
        switch
        {
            "image/png" =>
                ".png",

            "image/webp" =>
                ".webp",

            "image/gif" =>
                ".gif",

            "image/jpeg" =>
                ".jpg",

            "image/jpg" =>
                ".jpg",

            _ =>
                ".jpg"
        };
}

// ======================================================
// NORMAL AI RESPONSE
// ======================================================

async Task<string> GenerateNormalResponseAsync(
    long chatId,
    BinaryData? image,
    CancellationToken cancellationToken)
{
    string context =
        await BuildContextAsync(
            chatId
        );

    string prompt =
        $$"""
        You are TwoDay AI.

        You are the third member of a two-person game studio team.

        You are not the personal assistant of either founder.
        You have your own independent judgment.

        CORE BEHAVIOR:

        - Do not automatically agree.
        - Challenge weak assumptions.
        - If an idea is bad, say so clearly but constructively.
        - Suggest radically different approaches when justified.
        - Consider cost, time, risk, opportunity cost and expected value.
        - Help with calculations.
        - Separate facts, assumptions and opinions.
        - Never invent analytics or market data.
        - If current information is needed, tell the team to use
          the "araştır" command.
        - You may disagree with both founders.
        - Your goal is the success of the studio.

        IMAGE BEHAVIOR:

        If an image is attached:

        - Inspect the image carefully.
        - Read visible UI, charts, analytics, numbers and text.
        - Use visible information as evidence.
        - Do not invent details that are not visible.
        - If the image contains analytics, identify useful patterns.
        - If it is game art or UI, give practical game-development feedback.
        - If no explicit question was written, explain the most
          useful things you notice.
        - If text is too small because the image was sent in low detail,
          say that you cannot confidently read it rather than guessing.
        - If necessary, ask the humans to resend a crop or clearer image.

        COMMUNICATION STYLE:

        - Talk naturally like a real teammate in Telegram.
        - Usually be concise.
        - Do not constantly use headings.
        - Do not repeat the entire conversation.
        - Do not introduce yourself.
        - Do not say "as an AI".
        - Use Turkish if they speak Turkish.
        - Use English if they speak English.
        - Mixed language is fine.

        LOCAL MEMORY:

        {{context}}

        Respond to the latest discussion.
        """;

    var options =
        new CreateResponseOptions
        {
            Model = MODEL
        };

    List<ResponseContentPart> parts =
    [
        ResponseContentPart.CreateInputTextPart(
            prompt
        )
    ];

    if (image != null)
    {
        parts.Add(
            ResponseContentPart.CreateInputImagePart(
                image,
                ResponseImageDetailLevel.Low
            )
        );
    }

    options.InputItems.Add(
        ResponseItem.CreateUserMessageItem(
            parts
        )
    );

    ResponseResult response =
        await ai.CreateResponseAsync(
            options,
            cancellationToken
        );

    return response.GetOutputText();
}

// ======================================================
// WEB RESEARCH RESPONSE
// ======================================================

async Task<string> GenerateResearchResponseAsync(
    long chatId,
    BinaryData? image,
    CancellationToken cancellationToken)
{
    string context =
        await BuildContextAsync(
            chatId
        );

    string prompt =
        $$"""
        You are TwoDay AI, the third member of a small game studio.

        The humans explicitly asked you to research something.

        Use web search.

        If an image is attached:

        - Analyze the image as part of the research request.
        - Read visible numbers, analytics, UI and text.
        - Combine image evidence with web evidence.
        - Do not invent image details.
        - If image details are too small to read confidently,
          state that limitation.

        RESEARCH RULES:

        - Search for current information.
        - Prefer primary and authoritative sources.
        - Cross-check important claims when possible.
        - Do not present uncertain information as certain.
        - Focus on the actual decision the team is trying to make.
        - Give practical conclusions.
        - If the team's assumption is wrong, say so.
        - Suggest radically different alternatives if evidence supports them.
        - Keep the answer compact enough for Telegram.
        - Mention the most important sources or organizations.
        - Never fabricate sources.

        LOCAL TEAM CONTEXT:

        {{context}}

        Research and answer the latest request.
        """;

    var options =
        new CreateResponseOptions
        {
            Model = MODEL
        };

    // Web sadece araştırma komutunda.
    options.Tools.Add(
        ResponseTool.CreateWebSearchTool()
    );

    List<ResponseContentPart> parts =
    [
        ResponseContentPart.CreateInputTextPart(
            prompt
        )
    ];

    if (image != null)
    {
        parts.Add(
            ResponseContentPart.CreateInputImagePart(
                image,
                ResponseImageDetailLevel.Low
            )
        );
    }

    options.InputItems.Add(
        ResponseItem.CreateUserMessageItem(
            parts
        )
    );

    ResponseResult response =
        await ai.CreateResponseAsync(
            options,
            cancellationToken
        );

    return response.GetOutputText();
}

// ======================================================
// SHOULD AI SPEAK?
// ======================================================

async Task<bool> ShouldAiReplyAsync(
    long chatId,
    CancellationToken cancellationToken)
{
    string context =
        await BuildContextAsync(
            chatId
        );

    string prompt =
        $$"""
        Decide whether the AI third teammate should join this
        Telegram conversation right now.

        Reply YES only if the AI would provide meaningful value.

        YES examples:

        - important decision
        - genuine question
        - disagreement
        - meaningful factual or logical mistake
        - game design discussion
        - business decision
        - analytics interpretation
        - budgeting
        - calculation
        - strategic warning
        - strong alternative idea

        NO examples:

        - greetings
        - jokes
        - casual conversation
        - acknowledgements
        - small talk
        - the AI only has something minor to add

        Do not be overeager.

        Return exactly:

        YES

        or

        NO

        Conversation:

        {{context}}
        """;

    var options =
        new CreateResponseOptions
        {
            Model = MODEL
        };

    options.InputItems.Add(
        ResponseItem.CreateUserMessageItem(
            prompt
        )
    );

    ResponseResult response =
        await ai.CreateResponseAsync(
            options,
            cancellationToken
        );

    string result =
        response
            .GetOutputText()
            .Trim()
            .ToUpperInvariant();

    return result.StartsWith(
        "YES"
    );
}

// ======================================================
// CONTEXT BUILDER
// ======================================================

async Task<string> BuildContextAsync(
    long chatId)
{
    string summary =
        await GetSummaryAsync(
            chatId
        )
        ?? "Henüz uzun dönem özeti yok.";

    List<MemoryMessage> messages =
        await GetRecentMessagesAsync(
            chatId,
            RECENT_MESSAGE_COUNT
        );

    StringBuilder builder =
        new();

    builder.AppendLine(
        "LONG-TERM CHAT SUMMARY:"
    );

    builder.AppendLine(
        summary
    );

    builder.AppendLine();

    builder.AppendLine(
        "RECENT MESSAGES:"
    );

    foreach (
        var message in messages)
    {
        builder.Append(
            message.Name
        );

        builder.Append(": ");

        builder.AppendLine(
            message.Content
        );
    }

    return builder.ToString();
}

// ======================================================
// AUTO SUMMARY
// ======================================================

async Task MaybeUpdateSummaryAsync(
    long chatId,
    CancellationToken cancellationToken)
{
    int humanMessageCount =
        await GetHumanMessageCountSinceSummaryAsync(
            chatId
        );

    if (humanMessageCount <
        SUMMARIZE_EVERY_HUMAN_MESSAGES)
    {
        return;
    }

    Console.WriteLine(
        "AI > uzun dönem hafıza güncelleniyor..."
    );

    string oldSummary =
        await GetSummaryAsync(
            chatId
        )
        ?? "";

    List<MemoryMessage> newMessages =
        await GetMessagesSinceLastSummaryAsync(
            chatId
        );

    StringBuilder conversation =
        new();

    foreach (
        var message in newMessages)
    {
        conversation.Append(
            message.Name
        );

        conversation.Append(": ");

        conversation.AppendLine(
            message.Content
        );
    }

    string prompt =
        $$"""
        Update the long-term memory summary for a small game studio
        Telegram group.

        Preserve only information likely to matter later.

        Include:

        - important decisions
        - goals
        - projects
        - games
        - useful metrics
        - budgets
        - plans
        - unresolved questions
        - meaningful disagreements
        - commitments
        - working preferences
        - important findings from image analysis

        Exclude:

        - greetings
        - jokes
        - small talk
        - repetitive discussion
        - local image file paths unless genuinely useful

        Do not invent anything.

        Keep the summary compact but useful.

        OLD SUMMARY:

        {{oldSummary}}

        NEW CONVERSATION:

        {{conversation}}

        Return only the updated summary.
        """;

    var options =
        new CreateResponseOptions
        {
            Model = MODEL
        };

    options.InputItems.Add(
        ResponseItem.CreateUserMessageItem(
            prompt
        )
    );

    ResponseResult response =
        await ai.CreateResponseAsync(
            options,
            cancellationToken
        );

    string newSummary =
        response
            .GetOutputText()
            .Trim();

    await SaveSummaryAsync(
        chatId,
        newSummary
    );

    Console.WriteLine(
        "AI > hafıza güncellendi."
    );
}

// ======================================================
// DATABASE INIT
// ======================================================

async Task InitializeDatabaseAsync()
{
    await using var connection =
        new SqliteConnection(
            connectionString
        );

    await connection.OpenAsync();

    await using var command =
        connection.CreateCommand();

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
        """;

    await command.ExecuteNonQueryAsync();
}

// ======================================================
// SAVE MESSAGE
// ======================================================

async Task SaveMessageAsync(
    long chatId,
    long userId,
    string userName,
    string role,
    string content)
{
    await using var connection =
        new SqliteConnection(
            connectionString
        );

    await connection.OpenAsync();

    await using var command =
        connection.CreateCommand();

    command.CommandText =
        """
        INSERT INTO Messages
        (
            ChatId,
            UserId,
            UserName,
            Role,
            Content,
            CreatedAt
        )
        VALUES
        (
            $chatId,
            $userId,
            $userName,
            $role,
            $content,
            $createdAt
        );
        """;

    command.Parameters.AddWithValue(
        "$chatId",
        chatId
    );

    command.Parameters.AddWithValue(
        "$userId",
        userId
    );

    command.Parameters.AddWithValue(
        "$userName",
        userName
    );

    command.Parameters.AddWithValue(
        "$role",
        role
    );

    command.Parameters.AddWithValue(
        "$content",
        content
    );

    command.Parameters.AddWithValue(
        "$createdAt",
        DateTimeOffset.UtcNow
            .ToString("O")
    );

    await command.ExecuteNonQueryAsync();
}

// ======================================================
// RECENT MESSAGES
// ======================================================

async Task<List<MemoryMessage>>
    GetRecentMessagesAsync(
        long chatId,
        int count)
{
    List<MemoryMessage> messages =
        new();

    await using var connection =
        new SqliteConnection(
            connectionString
        );

    await connection.OpenAsync();

    await using var command =
        connection.CreateCommand();

    command.CommandText =
        """
        SELECT
            Id,
            UserName,
            Role,
            Content,
            CreatedAt
        FROM Messages
        WHERE ChatId = $chatId
        ORDER BY Id DESC
        LIMIT $count;
        """;

    command.Parameters.AddWithValue(
        "$chatId",
        chatId
    );

    command.Parameters.AddWithValue(
        "$count",
        count
    );

    await using var reader =
        await command.ExecuteReaderAsync();

    while (
        await reader.ReadAsync())
    {
        messages.Add(
            new MemoryMessage(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)
            )
        );
    }

    messages.Reverse();

    return messages;
}

// ======================================================
// GET SUMMARY
// ======================================================

async Task<string?> GetSummaryAsync(
    long chatId)
{
    await using var connection =
        new SqliteConnection(
            connectionString
        );

    await connection.OpenAsync();

    await using var command =
        connection.CreateCommand();

    command.CommandText =
        """
        SELECT Summary
        FROM Summaries
        WHERE ChatId = $chatId;
        """;

    command.Parameters.AddWithValue(
        "$chatId",
        chatId
    );

    object? result =
        await command.ExecuteScalarAsync();

    if (result == null ||
        result == DBNull.Value)
    {
        return null;
    }

    return result.ToString();
}

// ======================================================
// SAVE SUMMARY
// ======================================================

async Task SaveSummaryAsync(
    long chatId,
    string summary)
{
    long lastMessageId =
        await GetLastMessageIdAsync(
            chatId
        );

    await using var connection =
        new SqliteConnection(
            connectionString
        );

    await connection.OpenAsync();

    await using var command =
        connection.CreateCommand();

    command.CommandText =
        """
        INSERT INTO Summaries
        (
            ChatId,
            Summary,
            UpdatedAt,
            LastMessageId
        )
        VALUES
        (
            $chatId,
            $summary,
            $updatedAt,
            $lastMessageId
        )
        ON CONFLICT(ChatId)
        DO UPDATE SET
            Summary = excluded.Summary,
            UpdatedAt = excluded.UpdatedAt,
            LastMessageId = excluded.LastMessageId;
        """;

    command.Parameters.AddWithValue(
        "$chatId",
        chatId
    );

    command.Parameters.AddWithValue(
        "$summary",
        summary
    );

    command.Parameters.AddWithValue(
        "$updatedAt",
        DateTimeOffset.UtcNow
            .ToString("O")
    );

    command.Parameters.AddWithValue(
        "$lastMessageId",
        lastMessageId
    );

    await command.ExecuteNonQueryAsync();
}

// ======================================================
// HUMAN MESSAGE COUNT SINCE SUMMARY
// ======================================================

async Task<int>
    GetHumanMessageCountSinceSummaryAsync(
        long chatId)
{
    long lastSummaryMessageId =
        await GetLastSummaryMessageIdAsync(
            chatId
        );

    await using var connection =
        new SqliteConnection(
            connectionString
        );

    await connection.OpenAsync();

    await using var command =
        connection.CreateCommand();

    command.CommandText =
        """
        SELECT COUNT(*)
        FROM Messages
        WHERE ChatId = $chatId
          AND Id > $lastId
          AND Role = 'user';
        """;

    command.Parameters.AddWithValue(
        "$chatId",
        chatId
    );

    command.Parameters.AddWithValue(
        "$lastId",
        lastSummaryMessageId
    );

    object? result =
        await command.ExecuteScalarAsync();

    return Convert.ToInt32(
        result
    );
}

// ======================================================
// MESSAGES SINCE SUMMARY
// ======================================================

async Task<List<MemoryMessage>>
    GetMessagesSinceLastSummaryAsync(
        long chatId)
{
    long lastSummaryMessageId =
        await GetLastSummaryMessageIdAsync(
            chatId
        );

    List<MemoryMessage> messages =
        new();

    await using var connection =
        new SqliteConnection(
            connectionString
        );

    await connection.OpenAsync();

    await using var command =
        connection.CreateCommand();

    command.CommandText =
        """
        SELECT
            Id,
            UserName,
            Role,
            Content,
            CreatedAt
        FROM Messages
        WHERE ChatId = $chatId
          AND Id > $lastId
        ORDER BY Id ASC;
        """;

    command.Parameters.AddWithValue(
        "$chatId",
        chatId
    );

    command.Parameters.AddWithValue(
        "$lastId",
        lastSummaryMessageId
    );

    await using var reader =
        await command.ExecuteReaderAsync();

    while (
        await reader.ReadAsync())
    {
        messages.Add(
            new MemoryMessage(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)
            )
        );
    }

    return messages;
}

// ======================================================
// LAST SUMMARY MESSAGE ID
// ======================================================

async Task<long>
    GetLastSummaryMessageIdAsync(
        long chatId)
{
    await using var connection =
        new SqliteConnection(
            connectionString
        );

    await connection.OpenAsync();

    await using var command =
        connection.CreateCommand();

    command.CommandText =
        """
        SELECT LastMessageId
        FROM Summaries
        WHERE ChatId = $chatId;
        """;

    command.Parameters.AddWithValue(
        "$chatId",
        chatId
    );

    object? result =
        await command.ExecuteScalarAsync();

    if (result == null ||
        result == DBNull.Value)
    {
        return 0;
    }

    return Convert.ToInt64(
        result
    );
}

// ======================================================
// LAST MESSAGE ID
// ======================================================

async Task<long> GetLastMessageIdAsync(
    long chatId)
{
    await using var connection =
        new SqliteConnection(
            connectionString
        );

    await connection.OpenAsync();

    await using var command =
        connection.CreateCommand();

    command.CommandText =
        """
        SELECT COALESCE(MAX(Id), 0)
        FROM Messages
        WHERE ChatId = $chatId;
        """;

    command.Parameters.AddWithValue(
        "$chatId",
        chatId
    );

    object? result =
        await command.ExecuteScalarAsync();

    return Convert.ToInt64(
        result
    );
}

// ======================================================
// RESEARCH TRIGGER
// ======================================================

bool IsResearchRequest(
    string text)
{
    if (string.IsNullOrWhiteSpace(
        text))
    {
        return false;
    }

    string normalized =
        text
            .Trim()
            .ToLowerInvariant();

    string[] triggers =
    {
        "araştır",
        "arastir",
        "research ",
        "research:",
        "webden bak",
        "web'den bak",
        "internetten bak",
        "internetten araştır",
        "internetten arastir"
    };

    foreach (
        string trigger in triggers)
    {
        if (normalized.Contains(
            trigger))
        {
            return true;
        }
    }

    return false;
}

// ======================================================
// BOT MENTION
// ======================================================

bool IsBotMentioned(
    string text,
    string username)
{
    if (string.IsNullOrWhiteSpace(
        text))
    {
        return false;
    }

    if (string.IsNullOrWhiteSpace(
        username))
    {
        return false;
    }

    return text.Contains(
        $"@{username}",
        StringComparison.OrdinalIgnoreCase
    );
}

// ======================================================
// DISPLAY NAME
// ======================================================

string GetSenderName(
    User? user)
{
    if (user == null)
        return "Unknown";

    string fullName =
        $"{user.FirstName} {user.LastName}"
            .Trim();

    if (!string.IsNullOrWhiteSpace(
        fullName))
    {
        return fullName;
    }

    if (!string.IsNullOrWhiteSpace(
        user.Username))
    {
        return user.Username;
    }

    return $"User-{user.Id}";
}

// ======================================================
// TELEGRAM ERROR
// ======================================================

Task HandleErrorAsync(
    ITelegramBotClient bot,
    Exception exception,
    CancellationToken cancellationToken)
{
    Console.WriteLine();
    Console.WriteLine(
        "TELEGRAM ERROR:"
    );

    Console.WriteLine(
        exception
    );

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
    string CreatedAt
);

record ImagePayload(
    BinaryData Data,
    string LocalPath,
    string MediaType
);