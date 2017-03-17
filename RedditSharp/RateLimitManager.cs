using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Net.Http;
using System.Threading.Tasks;

namespace RedditSharp {

    /// <summary>;
    /// The method by which the WebAgent will limit request rate
    /// </summary>
    public enum RateLimitMode
    {
        /// <summary>
        /// Limits requests to one every two seconds (one if OAuth)
        /// </summary>
        Pace,
        /// <summary>
        /// Restricts requests to five per ten seconds (ten if OAuth)
        /// </summary>
        SmallBurst,
        /// <summary>
        /// Restricts requests to thirty per minute (sixty if OAuth)
        /// </summary>
        Burst,
        /// <summary>
        /// Does not restrict request rate. ***NOT RECOMMENDED***
        /// </summary>
        None
    }

    public class RateLimitManager
    {
        // See https://github.com/reddit/reddit/wiki/API for more details.
        public int Used { get; private set; }
        public int Remaining { get; private set; }
        // Approximate seconds until the rate limit is reset.
        public DateTimeOffset Reset { get; private set;}

        /// </summary>
        /// It is strongly advised that you leave this set to Burst or Pace. Reddit bans excessive
        /// requests with extreme predjudice.
        /// </summary>
        public RateLimitMode Mode { get; set; }

        /// <summary>
        /// UTC DateTime of last request made to Reddit API
        /// </summary>
        public DateTime LastRequest { get; private set; }
        /// <summary>
        /// UTC DateTime of when the last burst started
        /// </summary>
        public DateTime BurstStart { get; private set; }
        /// <summary>
        /// Number of requests made during the current burst
        /// </summary>
        public int RequestsThisBurst { get; private set; }

        private SemaphoreSlim rateLimitLock;

        public RateLimitManager(RateLimitMode mode = RateLimitMode.Pace) {
            rateLimitLock = new SemaphoreSlim(1, 1);
            Reset = DateTimeOffset.UtcNow;
            Mode = mode;
        }

        public async Task CheckRateLimitAsync(bool oauth)
        {
              await rateLimitLock.WaitAsync().ConfigureAwait(false);
              try {
                if (Remaining <= 0 && DateTime.UtcNow < Reset) {
                    await Task.Delay(Reset - DateTime.UtcNow).ConfigureAwait(false);
                } else {
                  await EnforceRateLimit(oauth);
                }
              } finally {
                rateLimitLock.Release();
              }
        }

        /// <summary>
        /// Enforce the api throttle.
        /// </summary>
        async Task EnforceRateLimit(bool oauth)
        {
            var limitRequestsPerMinute = oauth ? 60.0 : 30.0;
            var requestTime = DateTime.UtcNow;
            switch (Mode)
            {
                case RateLimitMode.Pace:
                   await Task.Delay(
                       TimeSpan.FromSeconds(60 / limitRequestsPerMinute) -
                       (DateTime.UtcNow - LastRequest)).ConfigureAwait(false);
                   break;
                case RateLimitMode.SmallBurst:
                   //this is first request OR the burst expired
                   if (RequestsThisBurst <= 0 || (DateTime.UtcNow - BurstStart).TotalSeconds >= 10)
                   {
                       BurstStart = DateTime.UtcNow;
                       RequestsThisBurst = 0;
                   }
                   //limit has been reached
                   if (RequestsThisBurst >= limitRequestsPerMinute / 6.0)
                   {
                       await Task.Delay(DateTime.UtcNow - BurstStart - TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                       BurstStart = DateTime.UtcNow;
                       RequestsThisBurst = 0;
                   }
                   RequestsThisBurst++;
                   break;
                 case RateLimitMode.Burst:
                    //this is first request OR the burst expired
                    if (RequestsThisBurst <= 0 || (DateTime.UtcNow - BurstStart).TotalSeconds >= 60)
                    {
                        BurstStart = DateTime.UtcNow;
                        RequestsThisBurst = 0;
                    }
                    if (RequestsThisBurst >= limitRequestsPerMinute) //limit has been reached
                    {
                        await Task.Delay(DateTime.UtcNow - BurstStart - TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                        BurstStart = DateTime.UtcNow;
                        RequestsThisBurst = 0;
                    }
                    RequestsThisBurst++;
                    break;
             }
             LastRequest = requestTime;
         }

        public async Task ReadHeadersAsync(HttpResponseMessage response) {
              await rateLimitLock.WaitAsync().ConfigureAwait(false);
              try {
                IEnumerable<string> values; var headers = response.Headers;
                int used, remaining;
                if (headers.TryGetValues("X-Ratelimit-Used", out values)) {
                  used = int.Parse(values.First());
                } else {
                  return;
                }
                if (headers.TryGetValues("X-Ratelimit-Remaining", out values)) {
                  remaining = (int)double.Parse(values.First());
                } else {
                  return;
                }
                // Do not update values if they the limit has not been reset and
                // the show an impossible reduction in usage
                if (DateTime.UtcNow < Reset && (used < Used || remaining > Remaining))
                  return;
                Used = used;
                Remaining = remaining;
                if (headers.TryGetValues("X-Ratelimit-Reset", out values))
                  Reset = DateTime.UtcNow + TimeSpan.FromSeconds(int.Parse(values.First()));
              } finally {
                rateLimitLock.Release();
              }

        }

    }

}
