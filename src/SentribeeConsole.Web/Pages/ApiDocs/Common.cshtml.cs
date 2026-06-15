using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SentribeeConsole.Web.Pages.ApiDocs;

[AllowAnonymous]
public class CommonModel : PageModel
{
    public void OnGet()
    {
    }
}
