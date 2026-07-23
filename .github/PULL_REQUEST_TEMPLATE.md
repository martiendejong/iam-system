# Pull Request

## 📋 Description

Brief description of what this PR does.

**Fixes:** #(issue number)

## 🎯 Type of Change

- [ ] 🐛 Bug fix (non-breaking change that fixes an issue)
- [ ] ✨ New feature (non-breaking change that adds functionality)
- [ ] 💥 Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] 📝 Documentation update
- [ ] 🎨 Code style update (formatting, renaming)
- [ ] ♻️ Refactoring (no functional changes)
- [ ] ⚡ Performance improvement
- [ ] ✅ Test addition or update
- [ ] 🔧 Configuration change
- [ ] 🚀 CI/CD change

## 🧪 How Has This Been Tested?

Describe the tests you ran to verify your changes.

- [ ] Unit tests
- [ ] Integration tests
- [ ] E2E tests
- [ ] Manual testing

**Test Configuration:**
- OS: [e.g. macOS 14, Ubuntu 22.04]
- Browser: [e.g. Chrome 120, Firefox 121]
- Node.js version: [e.g. 20.10.0]
- .NET version: [e.g. 9.0.0]

**Test Cases:**
1. Test case 1
2. Test case 2
3. Test case 3

## ✅ Checklist

### Code Quality
- [ ] My code follows the code style of this project
- [ ] I have performed a self-review of my own code
- [ ] I have commented my code, particularly in hard-to-understand areas
- [ ] I have made corresponding changes to the documentation
- [ ] My changes generate no new warnings
- [ ] I have added tests that prove my fix is effective or that my feature works
- [ ] New and existing unit tests pass locally with my changes
- [ ] Any dependent changes have been merged and published

### Security
- [ ] No secrets (API keys, passwords) are committed
- [ ] No sensitive user data is logged
- [ ] Input validation is implemented where needed
- [ ] SQL queries are parameterized (no string concatenation)
- [ ] Authentication/authorization is properly handled
- [ ] CSRF protection is in place for state-changing operations

### Performance
- [ ] No unnecessary database queries
- [ ] No N+1 query problems
- [ ] Async/await is used correctly
- [ ] No blocking operations on main thread
- [ ] Caching is implemented where appropriate

### Documentation
- [ ] README.md updated (if needed)
- [ ] API documentation updated (if needed)
- [ ] Migration guide updated (if breaking changes)
- [ ] CHANGELOG.md updated

## 📸 Screenshots (if applicable)

**Before:**
![Before](url)

**After:**
![After](url)

## 🔗 Related Issues / PRs

- Related to #
- Depends on #
- Blocks #

## 📝 Additional Notes

Add any other context about the pull request here.

**Reviewer Notes:**
- Areas that need special attention
- Questions for reviewers
- Known limitations

## 🚀 Deployment Notes

**Database migrations:**
- [ ] No migrations needed
- [ ] Migrations included (backwards compatible)
- [ ] Migrations included (breaking changes - see notes below)

**Environment variables:**
- [ ] No new environment variables
- [ ] New environment variables (documented below)

**Dependencies:**
- [ ] No new dependencies
- [ ] New dependencies (listed below)

**Breaking changes:**
- [ ] No breaking changes
- [ ] Breaking changes (documented below)

---

## 📊 Performance Impact

**Load Test Results:**
- Requests per second: N/A
- Average response time: N/A
- Memory usage: N/A

**Database Query Performance:**
- Query count: N/A
- Execution time: N/A

---

## 🎓 What I Learned

Share any interesting insights or challenges you encountered while working on this PR.

---

**By submitting this pull request, I confirm that:**
- [ ] I have read and agree to the [Code of Conduct](../CODE_OF_CONDUCT.md)
- [ ] I have read the [Contributing Guidelines](../CONTRIBUTING.md)
- [ ] My contribution is licensed under the MIT License
- [ ] I give permission for my contribution to be used in IAM System

---

**For Maintainers:**

**Review Checklist:**
- [ ] Code quality meets standards
- [ ] Tests are adequate
- [ ] Documentation is complete
- [ ] Security considerations addressed
- [ ] Performance impact acceptable
- [ ] Breaking changes documented
- [ ] Migration path clear

**Merge Status:**
- [ ] All CI checks passing
- [ ] Approved by 2+ reviewers
- [ ] No merge conflicts
- [ ] Changelog updated
