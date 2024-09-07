using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using AdvancedBot.Core.Entities;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace AdvancedBot.Core.Services
{
    public class PaginatorService
    {
        private readonly List<PaginatedMessage> _activeMessages;
        private readonly ConcurrentDictionary<ulong, Timer> _activeTimers;
        private readonly DiscordSocketClient _client;

        public PaginatorService(DiscordSocketClient client)
        {
            _activeMessages = new List<PaginatedMessage>();
            _activeTimers = new ConcurrentDictionary<ulong, Timer>();
            
            _client = client;
            _client.InteractionCreated += OnInteraction;
        }
        public async Task HandleNewPaginatedMessageAsync(SocketInteractionContext context, IEnumerable<EmbedField> displayFields, IEnumerable<string> displayTexts, Embed embed)
        {
            var message = await context.Interaction.GetOriginalResponseAsync();
            await context.Interaction.ModifyOriginalResponseAsync(msg => msg.Embeds = new Embed[] { embed });

            var paginatedMessage = new PaginatedMessage()
            {
                DiscordMessageId = message.Id,
                DiscordChannelId = context.Interaction.Channel.Id,
                DiscordUserId = context.User.Id,
            };

            if (displayFields == null) paginatedMessage.DisplayMessages = displayTexts.ToArray();
            else paginatedMessage.DisplayFields = displayFields.ToArray();

            _activeMessages.Add(paginatedMessage);
            
            if (paginatedMessage.TotalPages > 1)
            {
                await context.Interaction.ModifyOriginalResponseAsync(msg => msg.Components = CreateMessageComponents());
                AddNewTimer(message.Id);
            }

            await GoToFirstPageAsync(context.Interaction, message.Id);
        }

        public void AddNewTimer(ulong messageId)
        {
            var timer = new Timer(30 * 60 * 1000);
            timer.Start();

            timer.Elapsed += DisposeActivePaginatorMessage;
            _activeTimers.TryAdd(messageId, timer);
        }

        private async void DisposeActivePaginatorMessage(object timerObj, ElapsedEventArgs e)
        {
            var timer = timerObj as Timer;
            
            var messageId = _activeTimers.First(x => x.Value == timer).Key;
            timer.Enabled = false;

            var paginatorMessage = _activeMessages.First(x => x.DiscordMessageId == messageId);
            
            var channel = await _client.GetChannelAsync(paginatorMessage.DiscordChannelId) as SocketTextChannel;
            if (await channel.GetMessageAsync(paginatorMessage.DiscordMessageId) is not SocketUserMessage message) return;

            await message.ModifyAsync(x => x.Components = CreateMessageComponents(true));

            _activeMessages.Remove(paginatorMessage);
            _activeTimers.TryRemove(messageId, out Timer oldTimer);
            timer.Dispose();
        }

        public void ResetTimer(ulong messageId)
        {
            _activeTimers.TryRemove(messageId, out Timer currentTimer);

            currentTimer.Stop();
            currentTimer.Start();
            
            _activeTimers.TryAdd(messageId, currentTimer);
        }

        private async Task OnInteraction(SocketInteraction interaction)
        {
            if (interaction is SocketMessageComponent component)
            {
                // Not our message to handle
                if (_activeMessages.FirstOrDefault(x => x.DiscordMessageId == component.Message.Id) == null) return;

                await component.DeferAsync();

                switch (component.Data.CustomId)
                {
                    case "first":
                        await GoToFirstPageAsync(interaction, component.Message.Id);
                        break;
                    case "previous":
                        await GoToPreviousPageAsync(interaction, component.Message.Id);
                        break;
                    case "next":
                        await GoToNextPageAsync(interaction, component.Message.Id);
                        break;
                    case "last":
                        await GoToLastPageAsync(interaction, component.Message.Id);
                        break;
                }

                ResetTimer(component.Message.Id);
            }
        }

        private static MessageComponent CreateMessageComponents(bool disabled = false)
        {
            var builder = new ComponentBuilder()
                .WithButton("First", "first", ButtonStyle.Secondary, new Emoji("⏮️"), disabled: disabled)
                .WithButton("Previous", "previous", ButtonStyle.Secondary, new Emoji("⬅️"), disabled: disabled)
                .WithButton("Next", "next", ButtonStyle.Secondary, new Emoji("➡️"), disabled: disabled)
                .WithButton("Last", "last", ButtonStyle.Secondary, new Emoji("⏭️"), disabled: disabled);

            return builder.Build();
        }

        private static async Task HandleUpdateMessagePagesAsync(SocketInteraction interaction, PaginatedMessage msg)
        { 
            var channel = interaction.Channel;
            var message = await channel.GetMessageAsync(msg.DiscordMessageId);           

            var originalEmbed = message.Embeds.First();

            var updatedEmbed = new EmbedBuilder()
                .WithTitle($"{originalEmbed.Title.Split('(').First().Trim()} (Page {msg.CurrentPage})")
                .WithColor(originalEmbed.Color ?? Color.DarkBlue)
                .WithThumbnailUrl(originalEmbed.Thumbnail?.Url)
                .WithFooter(originalEmbed.Footer?.Text, originalEmbed.Footer?.IconUrl)
                .WithUrl(originalEmbed.Url);

            if (originalEmbed.Timestamp.HasValue) updatedEmbed.WithCurrentTimestamp();
            
            if (msg.DisplayMessages != null)
            {
                // get correct messages to display
                var displayMessages = msg.DisplayMessages.Skip((msg.CurrentPage - 1) * 10).Take(10);
                
                updatedEmbed.Description = string.Join("\n", displayMessages);
            }
            else
            {
                var displayFields = msg.DisplayFields.Skip((msg.CurrentPage - 1) * 10).Take(10).ToArray();

                for (int i = 0; i < displayFields.Length; i++)
                {
                    updatedEmbed.AddField(displayFields[i].Name, displayFields[i].Value, displayFields[i].Inline);
                }
            }

            await channel.ModifyMessageAsync(msg.DiscordMessageId, msg => msg.Embeds = new Embed[] { updatedEmbed.Build() });
        }

        private async Task GoToLastPageAsync(SocketInteraction interaction, ulong id)
        {
            var paginatorMessage = _activeMessages.Find(x => x.DiscordMessageId == id);
            paginatorMessage.CurrentPage = paginatorMessage.TotalPages;
            await HandleUpdateMessagePagesAsync(interaction, paginatorMessage);
        }

        private async Task GoToFirstPageAsync(SocketInteraction interaction, ulong id)
        {
            var paginatorMessage = _activeMessages.Find(x => x.DiscordMessageId == id);
            paginatorMessage.CurrentPage = 1;
            await HandleUpdateMessagePagesAsync(interaction, paginatorMessage);
        }

        private async Task GoToNextPageAsync(SocketInteraction interaction, ulong id)
        {
            var paginatorMessage = _activeMessages.First(x => x.DiscordMessageId == id);
            paginatorMessage.CurrentPage++;
            await HandleUpdateMessagePagesAsync(interaction, paginatorMessage);
        }

        private async Task GoToPreviousPageAsync(SocketInteraction interaction, ulong id)
        {
            var paginatorMessage = _activeMessages.Find(x => x.DiscordMessageId == id);
            paginatorMessage.CurrentPage--;
            await HandleUpdateMessagePagesAsync(interaction, paginatorMessage);
        }
    }
}
