﻿using System;
using System.Threading.Tasks;
using Edi.Captcha;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Moonglade.Configuration.Abstraction;
using Moonglade.Core;
using Moonglade.Core.Notification;
using Moonglade.Model;
using Moonglade.Web.Models;

namespace Moonglade.Web.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/comment")]
    public class CommentController : ControllerBase
    {
        #region Private Fields

        private readonly CommentService _commentService;
        private readonly IBlogNotificationClient _notificationClient;
        private readonly IBlogConfig _blogConfig;
        private bool DNT => (bool)HttpContext.Items["DNT"];

        #endregion

        public CommentController(
            CommentService commentService,
            IBlogConfig blogConfig,
            IBlogNotificationClient notificationClient = null)
        {
            _blogConfig = blogConfig;

            _commentService = commentService;
            _notificationClient = notificationClient;
        }

        [HttpPost]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> NewComment(
            [FromForm] PostSlugViewModelWrapper model, [FromServices] ISessionBasedCaptcha captcha)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            if (!_blogConfig.ContentSettings.EnableComments) return Forbid();

            if (!captcha.ValidateCaptchaCode(model.NewCommentViewModel.CaptchaCode, HttpContext.Session))
            {
                ModelState.AddModelError(nameof(model.NewCommentViewModel.CaptchaCode), "Wrong Captcha Code");
                return Conflict(ModelState);
            }

            var comment = model.NewCommentViewModel;
            var response = await _commentService.CreateAsync(new CommentRequest(comment.PostId)
            {
                Username = comment.Username,
                Content = comment.Content,
                Email = comment.Email,
                IpAddress = DNT ? "N/A" : HttpContext.Connection.RemoteIpAddress.ToString()
            });

            if (_blogConfig.NotificationSettings.SendEmailOnNewComment && null != _notificationClient)
            {
                _ = Task.Run(async () =>
                {
                    await _notificationClient.NotifyCommentAsync(response,
                        s => ContentProcessor.MarkdownToContent(s, ContentProcessor.MarkdownConvertType.Html));
                });
            }

            if (_blogConfig.ContentSettings.RequireCommentReview)
            {
                return Created("moonglade://empty", response);
            }

            return Ok();
        }

        [HttpPost("set-approval-status/{commentId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> SetApprovalStatus(Guid commentId)
        {
            if (commentId == Guid.Empty)
            {
                ModelState.AddModelError(nameof(commentId), "value is empty");
                return BadRequest(ModelState);
            }

            await _commentService.ToggleApprovalAsync(new[] { commentId });
            return Ok(commentId);
        }

        [HttpDelete("delete")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Delete([FromBody] Guid[] commentIds)
        {
            if (commentIds.Length == 0)
            {
                ModelState.AddModelError(nameof(commentIds), "value is empty");
                return BadRequest(ModelState);
            }

            await _commentService.DeleteAsync(commentIds);
            return Ok(commentIds);
        }

        [HttpPost("reply")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> Reply(
            [FromForm] Guid commentId, [FromForm] string replyContent, [FromServices] LinkGenerator linkGenerator)
        {
            if (commentId == Guid.Empty) ModelState.AddModelError(nameof(commentId), "value is empty");
            if (string.IsNullOrWhiteSpace(replyContent)) ModelState.AddModelError(nameof(replyContent), "value is empty");
            if (!ModelState.IsValid) return BadRequest(ModelState);

            if (!_blogConfig.ContentSettings.EnableComments) return Forbid();

            var reply = await _commentService.AddReply(commentId, replyContent);
            if (_blogConfig.NotificationSettings.SendEmailOnCommentReply && !string.IsNullOrWhiteSpace(reply.Email))
            {
                var postLink = GetPostUrl(linkGenerator, reply.PubDateUtc, reply.Slug);
                _ = Task.Run(async () =>
                {
                    await _notificationClient.NotifyCommentReplyAsync(reply, postLink);
                });
            }

            return Ok(reply);
        }

        private string GetPostUrl(LinkGenerator linkGenerator, DateTime pubDate, string slug)
        {
            var link = linkGenerator.GetUriByAction(HttpContext, "Slug", "Post",
                new
                {
                    year = pubDate.Year,
                    month = pubDate.Month,
                    day = pubDate.Day,
                    slug
                });
            return link;
        }
    }
}