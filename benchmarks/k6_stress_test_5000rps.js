import http from 'k6/http';
import { check, sleep } from 'k6';

// k6 Load Test Script: Simulating 5,000 RPS flash-sale peak on Con Cung Voucher Engine
export const options = {
  scenarios: {
    constant_request_rate: {
      executor: 'constant-arrival-rate',
      rate: 5000,           // 5,000 requests per second
      timeUnit: '1s',
      duration: '30s',
      preAllocatedVUs: 200,
      maxVUs: 1000,
    },
  },
  thresholds: {
    http_req_duration: ['p(95)<30', 'p(99)<45'], // 99% of requests must resolve under 45ms!
    http_req_failed: ['rate<0.01'],              // Zero server errors tolerated
  },
};

const BASE_URL = __ENV.API_URL || 'http://localhost:5000';

export default function () {
  const iteration = __VU * 100000 + __ITER;
  
  const payload = JSON.stringify({
    VoucherCode: 'CONCUNG50K',
    CustomerId: `CUST_${iteration}`,
    OrderId: `ORD_${iteration}`,
    StoreId: `STORE_${(__VU % 700) + 1}`, // Distributed across 700+ stores
    OrderAmount: 250000,
    IdempotencyKey: `IDEM_${iteration}`
  });

  const params = {
    headers: {
      'Content-Type': 'application/json',
    },
  };

  const res = http.post(`${BASE_URL}/api/v1/vouchers/redeem`, payload, params);

  check(res, {
    'status is 200 or 400 (expected business reject when quota depleted)': (r) => r.status === 200 || r.status === 400,
    'response under 45ms': (r) => r.timings.duration < 45,
  });
}
