﻿using Telegram.Bot;
using Microsoft.Extensions.Logging;
using HrukniHohlinaBot.DB.Models;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types;
using HrukniHohlinaBot.Services.Interfaces;

namespace HrukniHohlinaBot.Services.BotServices
{
    public class TelegramBotService : ITelegramBotService
    {
        private ITelegramBotClient _botClient;
        private readonly ILogger<TelegramBotService> _logger;
        private IHoholService _hoholService;
        private IUnitOfWork _unitOfWork;

        public TelegramBotService(ITelegramBotClient botClient, ILogger<TelegramBotService> logger,
            IHoholService hoholService, IUnitOfWork unitOfWork)
        {
            _logger = logger;
            _botClient = botClient;
            _hoholService = hoholService;
            _unitOfWork = unitOfWork;
        }

        public async Task StartBotAsync(CancellationToken cancellationToken)
        {
            new Thread(() => SetNewHohols()).Start();
            int offset = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var updates = await _botClient.GetUpdatesAsync(offset, timeout: 100, cancellationToken: cancellationToken);
                    foreach (var update in updates)
                    {
                        await HandleUpdate(update);
                        offset = update.Id + 1;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"An error occurred: {ex.Message}");
                }
            }
        }

        #region updates_handling

        static string[] claimMessages = new string[]
        {
            "похрюкай",
            "похрюкай хохол",
            "хохолина хрюкай",
            "хрюкай",
            "давай хрюкай",
            "скажи хрю хрю",
            "хрююююю",
            "ги ги га га",
        };

        static string[] allowationMessages = new string[]
        {
            @"Хороший хохол, Можеш хрюкать до {0}",
            @"Умничка, Хрюкаешь до {0}",
            @"Молодец, можеш розслабиться до {0}",
            @"До {0} можеш на совей свинячей балакать"
        };

        private async Task HandleUpdate(Update update)
        {
            Member? member = await GetNewMember(update);
            if (member == null) return;

            if (update.Type == UpdateType.Message
                && update.Message != null
                && update.Message.Text != null
                && update.Message.Text[0] == '/')
            {
                await HandleCommand(member, update.Message);
                return;
            }

            if (_unitOfWork.ChatService.Get(member.ChatId) == null) return;

            if (_unitOfWork.MemberService.Get(member.Id, member.ChatId) == null)
            {
                _unitOfWork.MemberService.Create(member);
                _unitOfWork.Commit();
                return;
            }

            if (update.Type == UpdateType.Message && update.Message != null && update.Message.Text == null)
            {
                await ChatMemberUpdate(update.Message);
            }
            else if (update.Type == UpdateType.Message && update.Message != null && update.Message.Text != null)
            {
                await MessageUpdate(update.Message, member);
            }
            else if (update.Type == UpdateType.MyChatMember && update.MyChatMember != null)
            {
                await MyChatMemberUpdate(update.MyChatMember, member);
            }
        }

        private async Task<Member?> GetNewMember(Update update)
        {
            long chatId;
            long memberId;
            if (update.Type == UpdateType.Message && update.Message != null)
            {
                Message? message = update.Message;
                chatId = message.Chat.Id;
                memberId = message.From.Id;
            }
            else if (update.Type == UpdateType.MyChatMember && update.MyChatMember != null)
            {
                ChatMemberUpdated? myChatMember = update.MyChatMember;
                chatId = myChatMember.Chat.Id;
                memberId = myChatMember.From.Id;
            }
            else return null;

            ChatMember? memberInfo = await _botClient.GetChatMemberAsync(chatId, memberId);
            Member member = new Member()
            {
                Username = memberInfo.User.Username,
                ChatId = chatId,
                Id = memberId,
                IsOwner = memberInfo.Status == ChatMemberStatus.Creator
            };

            return member;
        }

        private async Task HandleCommand(Member member, Message message)
        {
            if (message.Text[0] != '/') return;
            if (message.Text == "/start_hrukni" && member.IsOwner)
            {
                var chat = _unitOfWork.ChatService.Get(message.Chat.Id);
                if (chat == null)
                {
                    _unitOfWork.ChatService.Create(new DB.Models.Chat()
                    {
                        Id = message.Chat.Id,
                    });
                    _unitOfWork.Commit();
                }

                await _botClient.SendTextMessageAsync(message.Chat.Id, $"Хохлы...");
                await _botClient.SendTextMessageAsync(message.Chat.Id, $"Готовтесь хрюкать");
            }
            else if (message.Text == "/stop_hrukni" && member.IsOwner)
            {
                _unitOfWork.ChatService.Remove(message.Chat.Id);
                _unitOfWork.Commit();
            }
            else if (message.Text == "/reset_hohols" && member.IsOwner)
            {
                _hoholService.ResetHoholForChat(message.Chat.Id);
                _unitOfWork.Commit();
            }
        }

        private async Task ChatMemberUpdate(Message message)
        {
            if (message.NewChatMembers != null)
            {
                foreach (var newUser in message.NewChatMembers)
                {
                    if (newUser.IsBot) continue;

                    ChatMember? memberInfo = await _botClient.GetChatMemberAsync(message.Chat.Id, newUser.Id);
                    Member newMember = new Member()
                    {
                        Username = newUser.Username,
                        ChatId = message.Chat.Id,
                        Id = newUser.Id,
                        IsOwner = memberInfo.Status == ChatMemberStatus.Creator
                    };
                    _unitOfWork.MemberService.Create(newMember);
                    _unitOfWork.Commit();
                }
            }
            else if (message.LeftChatMember != null)
            {
                _unitOfWork.MemberService.Remove(message.LeftChatMember.Id, message.Chat.Id);
                _unitOfWork.Commit();
            }
        }

        private async Task MyChatMemberUpdate(ChatMemberUpdated myChatMember, Member member)
        {
            if (myChatMember.NewChatMember.Status == ChatMemberStatus.Left ||
            myChatMember.NewChatMember.Status == ChatMemberStatus.Kicked)
            {
                _unitOfWork.ChatService.Remove(myChatMember.Chat.Id);
                _unitOfWork.Commit();
            }
        }

        private async Task MessageUpdate(Message message, Member member)
        {
            if (message.Date.ToLocalTime().CompareTo(DateTime.Now.AddMinutes(-2)) > 0)
            {
                if (message.Text.ToLower().Contains("хрю")
                && !message.Text.ToLower().Contains("не")
                && !message.Text.ToLower().Contains("ні")
                && !message.Text.ToLower().Contains("нє"))
                {
                    var hohol = _hoholService.GetActiveHohol(message.Chat.Id);
                    if (hohol.Member.Id == message.From.Id)
                    {
                        if (hohol != null)
                        {
                            Random random = new Random();
                            int time = random.Next(2, 10);

                            hohol.EndWritingPeriod = DateTime.Now.ToUniversalTime().AddMinutes(time);
                            _hoholService.UpdateHohol(hohol);
                            _unitOfWork.Commit();

                            int i = random.Next(0, allowationMessages.Length);

                            var newDate = hohol.EndWritingPeriod.ToLocalTime().ToString("HH:mm:ss");
                            await _botClient.SendTextMessageAsync(
                            chatId: message.Chat.Id,
                                text: string.Format(allowationMessages[i], newDate),
                                replyParameters: message.MessageId
                            );
                        }
                    }
                }
                else
                {
                    var hohol = _hoholService.GetActiveHohol(message.Chat.Id);
                    if (hohol == null || hohol.Member.Id != message.From.Id) return;

                    if (!hohol.IsAllowedToWrite())
                    {
                        await _botClient.DeleteMessageAsync(
                            chatId: hohol.ChatId,
                            messageId: message.MessageId
                        );

                        Random rand = new Random();
                        int i = rand.Next(0, claimMessages.Length);

                        await _botClient.SendTextMessageAsync(
                            chatId: hohol.ChatId,
                            text: $"@{hohol.Member.Username} {claimMessages[i]}"
                        );
                    }
                }
            }
        }

        #endregion updates_handling



        #region reset_hohols

        private TimeOnly resetHoholsTime = new TimeOnly(6, 0, 0);
        private Dictionary<string, int> waitingTime = new Dictionary<string, int>()
        {
            { "second", 1000 },
            { "minute", 60000 },
            { "hour", 3600000 }
        };
        private void SetNewHohols()
        {
            while (true)
            {
                try
                {
                    var time = DateTime.Now.ToUniversalTime();

                    if (time.Hour == resetHoholsTime.Hour
                        && time.Minute == 0
                        && time.Second == 0)
                    {
                        _hoholService.ResetHohols();
                        _unitOfWork.Commit();
                    }

                    if (time.Second > 0) Thread.Sleep(waitingTime["second"]);
                    if (time.Minute > 0) Thread.Sleep(waitingTime["minute"]);
                    else Thread.Sleep(waitingTime["hour"]);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"An error occurred: {ex.Message}");
                }
            }
        }

        #endregion reset_hohols
    }
}
