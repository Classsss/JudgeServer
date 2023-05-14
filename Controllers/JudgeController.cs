using Microsoft.AspNetCore.Mvc;


namespace JudgeServer.Controllers {
    [Route("[controller]")]
    [ApiController]
    public class JudgeController : ControllerBase {
        // POST <JudgeController>
        [HttpPost]
        public async Task<IActionResult> Post([FromBody] JudgeRequest request) {
            Console.WriteLine("채점 시작");

            // 코드를 채점한 결과를 받는다.
            JudgeResult result = await Judge.JudgeCodeAsync(request);

            Console.WriteLine("채점 종료\n");

            return Ok(result);
        }
    }
}
