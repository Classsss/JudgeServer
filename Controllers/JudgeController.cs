using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace JudgeServer.Controllers {
    [Route("[controller]")]
    [ApiController]
    public class JudgeController : ControllerBase {
        private readonly ILogger<JudgeController> _logger;

        public JudgeController(ILogger<JudgeController> logger) {
            _logger = logger;
        }

        // POST <JudgeController>
        [HttpPost]
        public async Task<IActionResult> Post([FromBody] JudgeRequest request) {
            Console.WriteLine("Post 받음");
            _logger.LogInformation("Post 받음");
            _logger.LogWarning("Post 받음");
            _logger.LogError("Post 받음");

            // 코드를 채점한 결과를 받는다.
            JudgeResult result = await Judge.JudgeCodeAsync(request, _logger);

            return Ok(result);
        }
    }
}
