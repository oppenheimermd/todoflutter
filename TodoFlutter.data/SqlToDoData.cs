﻿using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using TodoFlutter.core.Models;
using TodoFlutter.core.Models.DTO;
using TodoFlutter.core.Models.GatwayResponses.Repositories;
using TodoFlutter.data.Helpers;
using TodoFlutter.data.Infrastructure;

namespace TodoFlutter.data
{
    public class SqlToDoData : IToDoData
    {
        private readonly ToDoDbContext _toDoDbContext;
        private readonly UserManager<AppUser> _userManager;
        private readonly ILogger<SqlToDoData> _logger;
        private readonly ITokenFactory _tokenFactory;
        private readonly IJwtFactory _jwtFactory;
        private readonly IJwtTokenValidator _jwtTokenValidator;

        public SqlToDoData(
            ToDoDbContext toDoDbContext,
            UserManager<AppUser> userManager,
            ILogger<SqlToDoData> logger,
            ITokenFactory tokenFactory,
            IJwtFactory jwtFactory,
            IJwtTokenValidator jwtTokenValidator
            )
        {
            this._toDoDbContext = toDoDbContext;
            this._userManager = userManager;
            this._logger = logger;
            this._tokenFactory = tokenFactory;
            this._jwtFactory = jwtFactory;
            this._jwtTokenValidator = jwtTokenValidator;
        }

        #region AppUser

        public async Task<CreateUserResponse> CreateUserAsync(string userName, string emailaddress, string Firsname, string password)
        {
            var appUser = new AppUser { Email = emailaddress, UserName = userName, Firstname = Firsname };
            //  To fix String and byte array keys are not client-generated by default errof
            //  See:https://docs.microsoft.com/en-us/ef/core/what-is-new/ef-core-3.x/breaking-changes#string-and-byte-array-keys-are-not-client-generated-by-default
            appUser.Id = Guid.NewGuid().ToString();
            var identityResult = await _userManager.CreateAsync(appUser, password);

            //  We have a probelm...
            if (!identityResult.Succeeded)
                return new CreateUserResponse(
                    appUser.Id,
                    false,
                    identityResult.Errors.Select(e => new Error(e.Code, e.Description)),
                    ResponseMessageTypes.USER_CREATED_FAILURE
                    );

            //  No issue, so add user to database
            _logger.LogInformation("User created a new account with password.");
            return new CreateUserResponse(
                appUser.Id,
                identityResult.Succeeded,
                identityResult.Succeeded ? null : identityResult.Errors.Select(e => new Error(e.Code, e.Description)),
                ResponseMessageTypes.USER_CREATED_SUCCESS);

        }

        public async Task<CreateUserLoginResponse> LoginUserAsync(string username, string password, string ipAddress)
        {
            var user = await _userManager.FindByEmailAsync(username);
            if (user != null)
            {
                //  Validate password
                var result = await _userManager.CheckPasswordAsync(user, password);
                if (result)
                {
                    //  Generate refresh token
                    var refreshToken = _tokenFactory.GenerateToken();
                    //  Add refresh token for user
                    await AddRefreshTokenAsync(
                        refreshToken,
                        Guid.Parse(user.Id),
                        ipAddress);

                    //  Generate accesss token
                    return new CreateUserLoginResponse(
                        await _jwtFactory.GenerateEncodedToken(
                            user.Id,
                            user.UserName,
                            user.Email
                            ),
                        refreshToken,
                        true,
                        null,
                        ResponseMessageTypes.USER_LOGIN_SUCCESS
                     );
                }
                //  Error
            }

            //error
            return new CreateUserLoginResponse(
                null,
                null,
                false,
                new[] { new Error("login_failure", "Invalid username or password.") }.ToList(),
                ResponseMessageTypes.USER_LOGIN_FAILURE
             );
        }

        #endregion

        #region RefreshToken

        public async Task AddRefreshTokenAsync(
            string refreshToken,
            Guid appUserId,
            string remoteIPAddress,
            double daysToExpire = 5
            )
        {
            RefreshToken newRefreshToken = new()
            {
                Created = DateTime.Now,
                Modified = DateTime.Now,
                Token = refreshToken,
                Expires = DateTime.UtcNow.AddDays(daysToExpire),
                AppUserId = appUserId,
                RemoteIpAddress = remoteIPAddress
            };

            await _toDoDbContext.RefreshTokens.AddAsync(newRefreshToken);
            await _toDoDbContext.SaveChangesAsync();
        }


        //  Refactor 
        //  https://jasonwatmore.com/post/2020/07/21/aspnet-core-3-create-and-validate-jwt-tokens-use-custom-jwt-middleware

        public async Task<RefreshTokenRespone> ExchangeRefreshTokenAsync(ExchangeRefreshTokenRequest exchangeRefreshToken)
        {
            var claimsPrincipal = _jwtTokenValidator.GetPrincipalFromToken(exchangeRefreshToken.AccessToken, exchangeRefreshToken.SigningKey);

            if (claimsPrincipal != null)
            {
                var id = claimsPrincipal.Claims.First(c => c.Type == "id");
                var user = await _userManager.FindByIdAsync(id.Value);
                if (user != null)
                {
                    //  Has refresh tokens?
                    var refreshTokens = await GetRefeshTokensForUserAsync(user.Id);
                    var validRefreshTokens = await HasValidRefreshTokenAsync(refreshTokens, exchangeRefreshToken.RefreshToken);
                    if (validRefreshTokens)
                    {
                        var jwtToken = await _jwtFactory.GenerateEncodedToken(user.Id, user.UserName, user.Email);
                        var refreshToken = _tokenFactory.GenerateToken();
                        // delete the old token we've exchanged
                        await RemoveRefreshTokenAsync(exchangeRefreshToken.RefreshToken, user);
                        // add the new one
                        await AddRefreshTokenAsync(refreshToken, Guid.Parse(user.Id), "");
                        var response = new RefreshTokenRespone(
                            jwtToken,
                            refreshToken,
                            true,
                            null,
                            ResponseMessageTypes.REFRESH_TOKEN_SUCCESS);

                        return response;
                    }

                    //  not sure logically if this is an error at this point
                    //  or if I should just return old tokens.  Not even
                    //  sure, wtf, will review with a fresh pair of eyes
                    //  tomorrow!!
                    /*var noRefreshTokens = new RefreshTokenRespone(
                        null,
                        null,
                        false,
                        new[] { new Error("refresh__token_failure", "Invalid or bad refresh token") }.ToList(),
                        ResponseMessageTypes.REFRESH_TOKEN_FAILURE
                        );
                    return failureRespone;*/
                }

            }            
            var failureRespone = new RefreshTokenRespone(
                null,
                null,
                false,
                new[] { new Error("refresh__token_failure", "Invalid or bad refresh token") }.ToList(),
                ResponseMessageTypes.REFRESH_TOKEN_FAILURE
                );
            return failureRespone;
        }

        public Task<bool> HasValidRefreshTokenAsync(List<RefreshToken> tokens, string refreshToken)
        {
            return Task.Run<bool>(() =>
            {
                return tokens.Any(rt => rt.Token == refreshToken && rt.Active);
            });
        }

        public async Task RemoveRefreshTokenAsync(string refreshToken, AppUser user)
        {
            var staleToken = await _toDoDbContext.RefreshTokens.Where(
                st => st.Token == refreshToken).FirstOrDefaultAsync();
            if(staleToken != null)
            {
                _toDoDbContext.RefreshTokens.Remove(staleToken);
                await _toDoDbContext.SaveChangesAsync();
            }
        }

        public async Task<List<RefreshToken>> GetRefeshTokensForUserAsync(string userId)
        {
            return await _toDoDbContext.RefreshTokens.Where(u => u.AppUserId == Guid.Parse(userId)).ToListAsync();
        }

        #endregion

        public async Task<Todo> GetByIdAsync(int id)
        {
            return await _toDoDbContext.ToDos.FindAsync(id);
        }

        public Todo Update(Todo todo)
        {
            var entity = _toDoDbContext.ToDos.Attach(todo);
            entity.State = EntityState.Modified;
            return todo;
        }

        public async Task<Todo> AddAsync(Todo todo)
        {
            await _toDoDbContext.AddAsync(todo);
            return todo;
        }

        public async Task<Todo> DeleteAsync(int id)
        {
            var todoToDelete = await GetByIdAsync(id);
            if (todoToDelete != null)
            {
                _toDoDbContext.ToDos.Remove(todoToDelete);
            }
            return todoToDelete;
        }

        public async Task<IEnumerable<Todo>> GetAllToDosByUserAsync(string userId)
        {
            var query = await _toDoDbContext.ToDos
                .Where(t => t.User.Id == userId)
                .OrderBy(t => t.Date)
                .ToListAsync();

            return query;
        }

        public IEnumerable<Todo> Find(Expression<Func<Todo, bool>> predicate)
        {
            return _toDoDbContext.Set<Todo>().Where(predicate);
        }

        public async Task<int> CommitAsync()
        {
            return await _toDoDbContext.SaveChangesAsync();
        }

        public async Task<int> GetTodosByUserCountAsync(string userId)
        {
            var todos = await _toDoDbContext.ToDos
                .Where(x => x.UserId == userId)
                .ToListAsync();
            return todos.Count;
        }

    }
}
