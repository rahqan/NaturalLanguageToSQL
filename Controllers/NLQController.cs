using Microsoft.AspNetCore.Mvc;
using Microsoft.SemanticKernel;
using System.Diagnostics;

namespace NaturalLanguageToSQL.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class NLQController : ControllerBase
    {
        private readonly Kernel _kernel;

        public NLQController(Kernel kernel)
        {
            _kernel = kernel;
        }

        [HttpGet]
        public async Task<IActionResult> Ask([FromQuery] string q)
        {
          Stopwatch stopwatch=  Stopwatch.StartNew();

            stopwatch.Start();
            var arguments = new KernelArguments 
            {
                ["question"] = q
            };

            var result = await _kernel.InvokeAsync(
                "NlqRetrievalPlugin",
                "AskDb",
                arguments
            );
            stopwatch.Stop();


            return Ok(new { result= result.GetValue<object>(), responseTime= stopwatch.ElapsedMilliseconds });
        }
    }
}
