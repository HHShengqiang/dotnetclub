using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Discussion.Core.Data;
using Discussion.Core.FileSystem;
using Discussion.Core.Models;
using Discussion.Core.Time;
using Discussion.Tests.Common;
using Discussion.Web.Services;
using Discussion.Web.Services.ChatHistoryImporting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using Xunit;
using MediaTypeHeaderValue = System.Net.Http.Headers.MediaTypeHeaderValue;

namespace Discussion.Web.Tests.Specs.Services
{
    [Collection("WebSpecs")]
    public class ChatHistoryImporterSpecs
    {
        private readonly IChatHistoryImporter _importer;
        private readonly IRepository<FileRecord> _fileRepo;
        private readonly IRepository<WeChatAccount> _weChatAccountRepo;

        public ChatHistoryImporterSpecs(TestDiscussionWebApp app)
        {
            var urlHelper = new Mock<IUrlHelper>();
            urlHelper.Setup(url => url.Action(It.IsAny<UrlActionContext>())).Returns("http://mock-url/");
                
            var httpClient = StubHttpClient.Create().When(req =>
            {
                var ms = new MemoryStream();
                ms.Write(Encoding.UTF8.GetBytes("This is file content"));
                ms.Seek(0, SeekOrigin.Begin);
                    
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(ms)
                    {
                        Headers =
                        {
                            ContentType = new MediaTypeHeaderValue("application/octet-stream")
                        }
                    }
                };
            });
                
            var currentUser = new Mock<ICurrentUser>();
            currentUser.SetupGet(u => u.DiscussionUser).Returns(new User {Id = 42});
                
            var options = new ChatyOptions
            {
                ServiceBaseUrl = "http://chaty/"
            };
            var optionsMock = new Mock<IOptions<ChatyOptions>>();
            optionsMock.SetupGet(o => o.Value).Returns(options);
            
            _fileRepo = app.GetService<IRepository<FileRecord>>();
            _weChatAccountRepo = app.GetService<IRepository<WeChatAccount>>();
            _importer = new DefaultChatHistoryImporter(app.GetService<IClock>(),
                httpClient,
                urlHelper.Object,
                _fileRepo,
                _weChatAccountRepo,
                app.GetService<IFileSystem>(),
                currentUser.Object,
                optionsMock.Object);
            
            app.DeleteAll<FileRecord>();
        }
        
        [Fact]
        public void should_import_sample_messages()
        {
            const string messageJsonResName = "Discussion.Web.Tests.Fixtures.SampleMessages.json";
            var resourceStream = typeof(ChatHistoryImporterSpecs).Assembly.GetManifestResourceStream(messageJsonResName);

            ChatMessage[] messages;
            using (var reader = new StreamReader(resourceStream, Encoding.UTF8))
            {
                var json = reader.ReadToEnd();
                messages = JsonConvert.DeserializeObject<ChatMessage[]>(json, new ChatMessageContentJsonConverter());
            }
            
            Assert.Equal(8, messages.Length);
            Assert.True(messages.All(msg => msg.Content != null));
            Assert.True(messages.All(msg => msg.SourceName != null));
            Assert.True(messages.All(msg => msg.SourceWxId != null));
        }
        
        [Fact]
        public async Task should_import_messages_as_replies()
        {
            var messages = new[]
            {
                new ChatMessage
                {
                    SourceName = "someone",
                    SourceTime = "2018/12/02 12:00:08",
                    SourceTimestamp = "1556067569000",
                    SourceWxId = "Wx_879LKJGSJJ",
                    Content = new TextChatMessageContent()
                    {
                        Text = "Hello WeChat Message"
                    }
                },
                new ChatMessage
                {
                    SourceName = "Another one",
                    SourceTime = "2018/12/02 12:00:09",
                    SourceTimestamp = "1546067539000",
                    SourceWxId = "Wx_879LKJGSJJ",
                    Content = new TextChatMessageContent()
                    {
                        Text = "The second Message"
                    }
                }
            };
            
            var importResult = await _importer.Import(messages);
            
            Assert.Equal(2, importResult.Count);
            Assert.Equal("Hello WeChat Message", importResult[0].Content);
            Assert.Equal("The second Message", importResult[1].Content);
        }
        
        [Fact]
        public async Task should_import_wechat_accounts_and_reuse_existing_account_records()
        {
            var messages = new[]
            {
                new ChatMessage
                {
                    SourceName = "someone",
                    SourceTime = "2018/12/02 12:00:08",
                    SourceTimestamp = "1556067569000",
                    SourceWxId = "Wx_879LKJGSJJ",
                    Content = new TextChatMessageContent()
                    {
                        Text = "Hello WeChat Message"
                    }
                },
                new ChatMessage
                {
                    SourceName = "Another one",
                    SourceTime = "2018/12/02 12:00:09",
                    SourceTimestamp = "1546067539000",
                    SourceWxId = "Wx_879LKJGSJJ",
                    Content = new TextChatMessageContent()
                    {
                        Text = "The second Message"
                    }
                }
            };
            
            var importResult = await _importer.Import(messages);
            
            Assert.NotNull(importResult[0].CreatedByWeChatAccount);
            Assert.Equal("someone", importResult[0].CreatedByWeChatAccount.DisplayName);
            Assert.Equal("Wx_879LKJGSJJ", importResult[0].CreatedByWeChatAccount.WxId);

            var wxAccounts = _weChatAccountRepo.All().ToList();
            Assert.Equal(1, wxAccounts.Count);
            Assert.Equal("Wx_879LKJGSJJ", wxAccounts[0].WxId);
            Assert.Equal("someone", wxAccounts[0].NickName);
        }
        
        [Fact]
        public async Task should_import_message_files()
        {
            var messages = new[]
            {
                new ChatMessage
                {
                    SourceName = "someone",
                    SourceTime = "2018/12/02 12:00:08",
                    SourceTimestamp = "1556067569000",
                    SourceWxId = "Wx_879LKJGSJJ",
                    Content = new FileChatMessageContent
                    {
                        Type = MessageType.Image,
                        FileId = "the-file-id",
                        FileName = "good-name.jpg"
                    }
                }
            };
            
            var importResult = await _importer.Import(messages);
            
            Assert.NotNull(importResult[0].Content);
            Assert.Equal("![good-name.jpg](http://mock-url/#middle)", importResult[0].Content);

            var files = _fileRepo.All().ToList();
            Assert.Equal(1, files.Count);
            Assert.Equal("imported-reply", files[0].Category);
        }
        
    }
}