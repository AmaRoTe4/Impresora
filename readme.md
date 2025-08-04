OPTIONS CONFIG:
1-

curl -X POST http://localhost:5000/config ^
     -H "Content-Type: application/json" ^
     -d "{\"printer\":\"POS-80\"}"

---

2-

Invoke-RestMethod -Method Post `
  -Uri "http://localhost:5000/config" `
  -Body '{"printer":"POS-80"}' `
  -ContentType "application/json"
  
---

TEST:
curl http://localhost:5000/printers
curl -X POST http://localhost:5000/print -d "TEST OK"

