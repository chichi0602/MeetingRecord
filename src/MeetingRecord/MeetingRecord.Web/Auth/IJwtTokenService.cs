using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Dtos.Auths;

namespace MeetingRecord.Web.Auth;

public interface IJwtTokenService
{
    TokenResponseDto CreateTokenResponse(MyUser user);

    CurrentUserDto ValidateRefreshToken(string refreshToken);
}
