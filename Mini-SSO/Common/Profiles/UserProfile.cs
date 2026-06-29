using AutoMapper;
using Mini_SSO.Model.Dtos;
using Mini_SSO.Model.Entities;

namespace Mini_SSO.Common.Profiles
{
    public class UserProfile : Profile
    {
        public UserProfile()
        {
            CreateMap<CreateUserDto, Users>();
        }
    }
}
