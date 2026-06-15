using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SentribeeConsole.Web.Pages.Operations.Events;

[Authorize]
public sealed class DetailsModel : PageModel
{
    public int EventId { get; private set; }

    public IActionResult OnGet(int id)
    {
        if (id <= 0)
        {
            return NotFound();
        }

        EventId = id;
        return Page();
    }
}
