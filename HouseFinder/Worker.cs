using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace HouseFinder
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private static readonly HttpClient client = new HttpClient();
        private readonly TelegramBotClient _botClient;

        public Worker(ILogger<Worker> logger)
        {
            // Runs at app startup
            _logger = logger;

            // Create SQlite tables if not already created
            using (var connection = new SqliteConnection("Data Source=houses.db"))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "CREATE TABLE IF NOT EXISTS houses(id INTEGER PRIMARY KEY)";

                    command.ExecuteNonQuery();
                }
            }

            using (var connection = new SqliteConnection("Data Source=chats.db"))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "CREATE TABLE IF NOT EXISTS chats(id INTEGER PRIMARY KEY)";

                    command.ExecuteNonQuery();
                }
            }


            _botClient = new TelegramBotClient("bot token here");

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>() // receive all update types
            };

            _botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                pollingErrorHandler: HandlePollingErrorAsync,
                receiverOptions: receiverOptions
            );

            // Let everyone know the app has started
            SendStringMessageToAllUsers("Appliction started");
        }

        async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            // Only process Message updates: https://core.telegram.org/bots/api#message
            if (update.Message is not { } message)
                return;
            // Only process text messages
            if (message.Text is not { } messageText)
                return;

            var chatId = message.Chat.Id;

            Console.WriteLine($"Received a '{messageText}' message in chat {chatId}.");

            // If the bot receives a message from a user then store it in the chats database so that any rightmove updates
            // can be sent to every chat
            using (var connection = new SqliteConnection("Data Source=chats.db"))
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM chats WHERE id=$id";
                    command.Parameters.AddWithValue("$id", chatId);

                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.HasRows == false)
                        {
                            using (var insertCommand = connection.CreateCommand())
                            {
                                insertCommand.CommandText = "INSERT OR REPLACE INTO chats(id) VALUES($id)";
                                insertCommand.Parameters.AddWithValue("$id", chatId);

                                await insertCommand.ExecuteNonQueryAsync();
                            }
                        }
                    }
                }
            }
        }

        Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            var ErrorMessage = exception switch
            {
                ApiRequestException apiRequestException
                    => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
                _ => exception.ToString()
            };

            Console.WriteLine(ErrorMessage);
            return Task.CompletedTask;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // This executes every 5 mins
                try
                {
                    // The Rightmove API is weird, the location identifier can be obtained from searching on the website for locations and looking
                    // at what the URL says. REGION%5E219 is the whole of Bristol and it was easier to just get notifications for the whole city, also 
                    // helped get an idea of prices
                    // sortType=6 is important as it's the sort that shows recently listed first, had to figure this out with trial and error
                    var json =
                        await client.GetStreamAsync(@"
                        https://api.rightmove.co.uk/api/rent/find?index=1&numberOfPropertiesRequested=50&locationIdentifier=REGION%5E219&apiApplication=IPAD&minBedrooms=2&maxBedrooms=2&dontShow=houseShare&maxPrice=1500&sortType=6");

                    var properties = await JsonSerializer.DeserializeAsync<RightMoveResult>(json);

                    // Get the rightmove IDs of all the properties in the JSON
                    var ids = properties.Properties.Select(p => p.Identifier);

                    using (var connection = new SqliteConnection("Data Source=houses.db"))
                    {
                        foreach (var id in ids)
                        {
                            await connection.OpenAsync();
                            using (var command = connection.CreateCommand())
                            {
                                // Search the database for a rightmove ID
                                command.CommandText = "SELECT * FROM houses WHERE id=$id";
                                command.Parameters.AddWithValue("$id", id);

                                using (var reader = command.ExecuteReader())
                                {
                                    // If ID not in database, store it and then send a message to notify of new property
                                    if (reader.HasRows == false)
                                    {
                                        using (var insertCommand = connection.CreateCommand())
                                        {
                                            insertCommand.CommandText = "INSERT OR REPLACE INTO houses(id) VALUES($id)";
                                            insertCommand.Parameters.AddWithValue("$id", id);

                                            await insertCommand.ExecuteNonQueryAsync();
                                        }

                                        // New house, send notification
                                        // IMPORTANT: uncomment the line below only once you've ran for the first time and populated the database with all current houses
                                        // otherwise you'll get spammed with hundreds of messages


                                        // await SendTelegramMessage(id);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    // Just log out any exceptions, without the catch the app will crash. Occasionally Rightmove or Telegram can time out
                    Console.WriteLine(e);
                }

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        private async Task SendTelegramMessage(int id)
        {
            // Get all chats and send the rightmove URL to all users
            using (var connection = new SqliteConnection("Data Source=chats.db"))
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM chats";

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var chatId = reader.GetInt64(0);

                            var message = await _botClient.SendTextMessageAsync(chatId: chatId, text: $"https://www.rightmove.co.uk/properties/{id}");
                        }
                    }
                }

            }
        }

        private async Task SendStringMessageToAllUsers(string message)
        {
            // Send a string to all users. Could refactor to remove duplication with above method but too lazy lol
            using (var connection = new SqliteConnection("Data Source=chats.db"))
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM chats";

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var chatId = reader.GetInt64(0);

                            await _botClient.SendTextMessageAsync(chatId: chatId, text: message);
                        }
                    }
                }
            }
        }
    }

    // These are just the values we care about from the API
    public class RightMoveResult
    {
        [JsonPropertyName("properties")]
        public List<Property> Properties { get; set; }
    }

    public class Property
    {
        [JsonPropertyName("identifier")]
        public int Identifier { get; set; }
    }
}
