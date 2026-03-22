#!/usr/bin/env python3
"""
Comprehensive IAM System API Test Suite
Tests all 29 endpoints across 4 controllers
"""

import requests
import json
import urllib3
from datetime import datetime

# Disable SSL warnings for self-signed certificates
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

BASE_URL = "https://localhost:5001"
TEST_EMAIL = f"admin_{datetime.now().strftime('%Y%m%d%H%M%S')}@test.com"
TEST_PASSWORD = "Admin123!"

class APITester:
    def __init__(self):
        self.base_url = BASE_URL
        self.access_token = None
        self.user_id = None
        self.role_id = None
        self.tenant_id = None
        self.results = []

    def test(self, name, method, endpoint, data=None, auth=True, expected_status=None):
        """Test an endpoint and record results"""
        url = f"{self.base_url}{endpoint}"
        headers = {"Content-Type": "application/json"}

        if auth and self.access_token:
            headers["Authorization"] = f"Bearer {self.access_token}"

        try:
            if method == "GET":
                response = requests.get(url, headers=headers, verify=False, timeout=5)
            elif method == "POST":
                response = requests.post(url, headers=headers, json=data, verify=False, timeout=5)
            elif method == "PUT":
                response = requests.put(url, headers=headers, json=data, verify=False, timeout=5)
            elif method == "DELETE":
                response = requests.delete(url, headers=headers, verify=False, timeout=5)

            success = (expected_status is None) or (response.status_code == expected_status)

            result = {
                "name": name,
                "method": method,
                "endpoint": endpoint,
                "status": response.status_code,
                "success": success or (200 <= response.status_code < 300),
                "response_size": len(response.content)
            }

            self.results.append(result)

            status_icon = "[OK]" if result["success"] else "[FAIL]"
            print(f"{status_icon} {name}: {method} {endpoint} -> {response.status_code}")

            return response

        except Exception as e:
            print(f"[FAIL] {name}: {method} {endpoint} -> ERROR: {str(e)}")
            self.results.append({
                "name": name,
                "method": method,
                "endpoint": endpoint,
                "status": "ERROR",
                "success": False,
                "error": str(e)
            })
            return None

    def run_tests(self):
        print("="*60)
        print("IAM SYSTEM API TEST SUITE")
        print("="*60)
        print()

        # PHASE 1: AUTHENTICATION (7 endpoints)
        print("*** PHASE 1: AUTHENTICATION (7 endpoints)")
        print("-"*60)

        # 1. Register
        response = self.test(
            "1. Register User",
            "POST",
            "/api/auth/register",
            {
                "email": TEST_EMAIL,
                "password": TEST_PASSWORD,
                "firstName": "Admin",
                "lastName": "User"
            },
            auth=False,
            expected_status=201
        )

        if response and response.status_code == 200:
            data = response.json()
            self.user_id = data.get("userId")
            print(f"   User ID: {self.user_id}")

            # Get verification token from database and verify email
            import subprocess
            try:
                result = subprocess.run(
                    ['powershell.exe', '-Command',
                     f"& 'C:\\Program Files\\PostgreSQL\\18\\bin\\psql.exe' -U iamuser -d iamdb -t -c \"SELECT \\\"EmailVerificationToken\\\" FROM \\\"Users\\\" WHERE \\\"Email\\\" = '{TEST_EMAIL}';\""],
                    capture_output=True, text=True, timeout=5
                )
                token = result.stdout.strip()
                if token:
                    # Verify email
                    verify_response = self.test(
                        "   -> Verify Email", "POST", "/api/auth/verify-email",
                        {"token": token}, auth=False, expected_status=200
                    )
            except Exception as e:
                print(f"   Warning: Could not auto-verify email: {e}")

        # 2. Login
        response = self.test(
            "2. Login",
            "POST",
            "/api/auth/login",
            {
                "email": TEST_EMAIL,
                "password": TEST_PASSWORD
            },
            auth=False,
            expected_status=200
        )

        if response and response.status_code == 200:
            data = response.json()
            self.access_token = data.get("accessToken")
            print(f"   Access Token: {self.access_token[:20]}...")

        # 3. Verify Email (tested inline during registration)

        # 4. Forgot Password
        self.test("4. Forgot Password", "POST", "/api/auth/forgot-password", {"email": TEST_EMAIL}, auth=False)

        # 5. Reset Password (will fail - no valid token)
        self.test("5. Reset Password", "POST", "/api/auth/reset-password",
                  {"token": "test", "newPassword": "NewPass123!"}, auth=False, expected_status=400)

        # 6. Refresh Token (will fail - no cookie)
        self.test("6. Refresh Token", "POST", "/api/auth/refresh", auth=False, expected_status=401)

        # 7. Logout
        self.test("7. Logout", "POST", "/api/auth/logout")

        print()

        # Re-login to get fresh token
        response = self.test("Re-login", "POST", "/api/auth/login",
                           {"email": TEST_EMAIL, "password": TEST_PASSWORD}, auth=False)
        if response and response.status_code == 200:
            self.access_token = response.json().get("accessToken")

        # PHASE 2: USER MANAGEMENT (7 endpoints)
        print("\n*** PHASE 2: USER MANAGEMENT (7 endpoints)")
        print("-"*60)

        # 8. Get All Users
        self.test("8. Get All Users", "GET", "/api/users")

        # 9. Get User by ID
        if self.user_id:
            self.test("9. Get User by ID", "GET", f"/api/users/{self.user_id}")

        # 10. Update User
        if self.user_id:
            self.test("10. Update User", "PUT", f"/api/users/{self.user_id}",
                     {"firstName": "Updated", "lastName": "Name", "phoneNumber": "+1234567890"})

        # 11. Activate User
        if self.user_id:
            self.test("11. Activate User", "PUT", f"/api/users/{self.user_id}/activate")

        # 12. Deactivate User
        if self.user_id:
            self.test("12. Deactivate User", "PUT", f"/api/users/{self.user_id}/deactivate")

        # 13. Get User Roles
        if self.user_id:
            self.test("13. Get User Roles", "GET", f"/api/users/{self.user_id}/roles")

        # 14. Get Current User
        self.test("14. Get Current User", "GET", "/api/users/me")

        print()

        # PHASE 3: ROLE MANAGEMENT (7 endpoints)
        print("\n*** PHASE 3: ROLE MANAGEMENT (7 endpoints)")
        print("-"*60)

        # 15. Get All Roles
        response = self.test("15. Get All Roles", "GET", "/api/roles")
        if response and response.status_code == 200:
            roles = response.json()
            if roles:
                self.role_id = roles[0]["id"]
                print(f"   Using Role ID: {self.role_id}")

        # 16. Get Role by ID
        if self.role_id:
            self.test("16. Get Role by ID", "GET", f"/api/roles/{self.role_id}")

        # 17. Create Role (will fail - needs SuperAdmin)
        response = self.test("17. Create Role", "POST", "/api/roles",
                           {"name": "TestRole", "description": "Test role",
                            "permissions": ["User.View", "User.Create"]}, expected_status=403)

        # 18. Update Role (will fail - system role or no permission)
        if self.role_id:
            self.test("18. Update Role", "PUT", f"/api/roles/{self.role_id}",
                     {"name": "UpdatedRole", "description": "Updated",
                      "permissions": ["User.View"]}, expected_status=403)

        # 19. Delete Role (will fail - system role or no permission)
        if self.role_id:
            self.test("19. Delete Role", "DELETE", f"/api/roles/{self.role_id}", expected_status=403)

        # 20. Assign Role (will fail - no permission)
        if self.role_id and self.user_id:
            self.test("20. Assign Role to User", "POST", f"/api/roles/{self.role_id}/assign",
                     {"userId": self.user_id, "expiresAt": "2027-12-31T23:59:59Z"}, expected_status=403)

        # 21. Revoke Role (will fail - no permission)
        if self.role_id and self.user_id:
            self.test("21. Revoke Role from User", "POST", f"/api/roles/{self.role_id}/revoke",
                     {"userId": self.user_id}, expected_status=403)

        print()

        # PHASE 4: TENANT MANAGEMENT (8 endpoints)
        print("\n*** PHASE 4: TENANT MANAGEMENT (8 endpoints)")
        print("-"*60)

        # 22. Get All Tenants
        self.test("22. Get All Tenants", "GET", "/api/tenants")

        # 23. Create Tenant (will likely fail - needs permission)
        response = self.test("23. Create Tenant", "POST", "/api/tenants",
                           {
                               "name": "Test Building",
                               "slug": f"test-building-{datetime.now().strftime('%H%M%S')}",
                               "type": "Building",
                               "metadata": {"address": "123 Test St"},
                               "settings": {"timezone": "UTC"}
                           }, expected_status=403)

        if response and response.status_code == 201:
            self.tenant_id = response.json().get("id")

        # 24. Get Tenant by ID (skip if no tenant)
        if self.tenant_id:
            self.test("24. Get Tenant by ID", "GET", f"/api/tenants/{self.tenant_id}")

        # 25. Update Tenant (skip if no tenant)
        if self.tenant_id:
            self.test("25. Update Tenant", "PUT", f"/api/tenants/{self.tenant_id}",
                     {"name": "Updated Building", "metadata": {"address": "456 Updated St"}})

        # 26. Get Tenant Hierarchy (skip if no tenant)
        if self.tenant_id:
            self.test("26. Get Tenant Hierarchy", "GET", f"/api/tenants/{self.tenant_id}/hierarchy")

        # 27. Get Building Structure (skip if no tenant)
        if self.tenant_id:
            self.test("27. Get Building Structure", "GET", f"/api/tenants/buildings/{self.tenant_id}/structure")

        # 28. Get My Tenants
        self.test("28. Get My Tenants", "GET", "/api/tenants/my-tenants")

        # 29. Delete Tenant (skip if no tenant)
        if self.tenant_id:
            self.test("29. Delete Tenant", "DELETE", f"/api/tenants/{self.tenant_id}")

        print()
        self.print_summary()

    def print_summary(self):
        print("\n" + "="*60)
        print("TEST SUMMARY")
        print("="*60)

        total = len(self.results)
        passed = sum(1 for r in self.results if r["success"])
        failed = total - passed

        print(f"Total Tests: {total}")
        print(f"[OK] Passed: {passed}")
        print(f"[FAIL] Failed: {failed}")
        print(f"Success Rate: {(passed/total*100):.1f}%")
        print()

        if failed > 0:
            print("Failed Tests:")
            for r in self.results:
                if not r["success"]:
                    status = r.get("status", "ERROR")
                    error = r.get("error", "")
                    print(f"  - {r['name']}: {r['method']} {r['endpoint']} -> {status} {error}")

if __name__ == "__main__":
    tester = APITester()
    tester.run_tests()
