using Microsoft.AspNetCore.Mvc;

namespace SentribeeConsole.Web.Pages.Crm;

public class LogoutModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public IActionResult OnPost()
    {
        SignOutMerchant();
        TempData["CrmAuthStatus"] = "Signed out.";
        return RedirectToPage("/Crm/Login");
    }
}
