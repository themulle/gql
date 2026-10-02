namespace GqlGateway.Application.Interfaces;

using System.Security.Claims;
using GqlGateway.Domain.Model;

public interface IIdentitySubjectResolver
{
    SubjectIdentity ResolveSubject(ClaimsPrincipal? principal);
}
