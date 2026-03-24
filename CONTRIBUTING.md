# Contributing to IAM System

Thank you for considering contributing to IAM System! We're building the open-source alternative to Auth0 and Azure AD, and we need your help.

## 🎯 Our Mission

**Rescue developers from Auth0 pricing traps and Azure AD complexity.**

We believe authentication should be:
- Open source (trust through transparency)
- Simple (60-second setup)
- Secure (passkeys-first, phishing-resistant)
- Free (10,000 MAU forever)

## 🤝 Ways to Contribute

### 1. Code Contributions

**We need help with:**
- 🐛 Bug fixes
- ✨ New features
- 🧪 Test coverage
- 📦 SDK improvements (React, Vue, Next.js, Angular)
- 🔒 Security enhancements
- ⚡ Performance optimizations
- 🌐 Internationalization (i18n)

### 2. Documentation

**We need help with:**
- 📖 Improving existing docs
- 🌍 Translations (French, German, Spanish, Dutch, etc.)
- 🎥 Video tutorials
- 📝 Blog posts about IAM System
- 💡 Examples and use cases

### 3. Community Support

**We need help with:**
- 💬 Answering questions on Discord
- 🐛 Triaging GitHub issues
- 🎓 Helping newcomers
- 📢 Spreading the word

### 4. Testing & Feedback

**We need help with:**
- 🧪 Testing new features
- 🐛 Reporting bugs
- 💡 Suggesting improvements
- 📊 Sharing your use case

---

## 🚀 Getting Started

### Prerequisites

- .NET 9 SDK
- Node.js 20+
- PostgreSQL 16 (or SQLite for development)
- Git

### Setup Development Environment

```bash
# 1. Fork the repository
# Click "Fork" on GitHub: https://github.com/martiendejong/iam-system

# 2. Clone your fork
git clone https://github.com/YOUR_USERNAME/iam-system.git
cd iam-system

# 3. Add upstream remote
git remote add upstream https://github.com/martiendejong/iam-system.git

# 4. Install backend dependencies
cd src/IAM.API
dotnet restore

# 5. Setup database
dotnet ef database update

# 6. Run backend
dotnet run
# API now running at https://localhost:5161

# 7. Install frontend dependencies (in another terminal)
cd src/IAM.Admin.Web
npm install

# 8. Run frontend
npm run dev
# Admin UI now running at http://localhost:5173
```

### Running Tests

```bash
# Unit tests
dotnet test

# Integration tests
cd tests/IAM.Integration.Tests
dotnet test

# E2E tests
cd tests/IAM.E2E.Tests
npm test
```

---

## 📝 Contribution Workflow

### Step 1: Create an Issue (Optional but Recommended)

Before starting work, create an issue to discuss your proposal:

```markdown
**Problem:** Auth0 charges $3,000/year for SMS MFA

**Proposed Solution:** Add free passkey support (Face ID/Touch ID)

**Benefits:**
- Saves users $3,000/year
- More secure (phishing-resistant)
- Better UX (1 tap vs typing code)

**Implementation Plan:**
1. Add WebAuthn endpoints
2. Update SDK with passkey methods
3. Add passkey UI to admin panel
```

**Why create an issue first?**
- Avoid duplicate work
- Get feedback before investing time
- Align with project direction

### Step 2: Create a Branch

```bash
# Update your fork
git checkout develop
git pull upstream develop

# Create feature branch
git checkout -b feature/your-feature-name
# Or: git checkout -b fix/bug-description
```

**Branch naming conventions:**
- `feature/` - New features
- `fix/` - Bug fixes
- `docs/` - Documentation changes
- `refactor/` - Code refactoring
- `test/` - Test additions
- `chore/` - Maintenance tasks

### Step 3: Make Your Changes

**Code Style:**
- C# - Follow Microsoft C# conventions
- TypeScript/JavaScript - Use Prettier (auto-formatted)
- Commits - Conventional Commits format

**Commit Message Format:**
```
type(scope): subject

body (optional)

footer (optional)
```

**Types:**
- `feat` - New feature
- `fix` - Bug fix
- `docs` - Documentation
- `style` - Code style (formatting)
- `refactor` - Code refactoring
- `test` - Tests
- `chore` - Maintenance

**Examples:**
```bash
git commit -m "feat(auth): Add passkey authentication support"
git commit -m "fix(api): Handle null refresh token gracefully"
git commit -m "docs(quickstart): Add Vue.js example"
```

### Step 4: Test Your Changes

```bash
# Run all tests
dotnet test
npm test

# Test manually
npm run dev
# Test in browser: http://localhost:5173
```

**Test Checklist:**
- [ ] Unit tests pass
- [ ] Integration tests pass
- [ ] Manual testing done
- [ ] No console errors
- [ ] Works in Chrome, Firefox, Safari
- [ ] Works on mobile (if UI change)

### Step 5: Push and Create Pull Request

```bash
# Push to your fork
git push origin feature/your-feature-name

# Create PR on GitHub
# 1. Go to https://github.com/martiendejong/iam-system
# 2. Click "New Pull Request"
# 3. Select your fork and branch
# 4. Fill in the template (see below)
```

**PR Template:**
```markdown
## Description
Brief description of what this PR does

## Type of Change
- [ ] Bug fix (non-breaking change)
- [ ] New feature (non-breaking change)
- [ ] Breaking change (fix or feature that breaks existing functionality)
- [ ] Documentation update

## How Has This Been Tested?
- [ ] Unit tests
- [ ] Integration tests
- [ ] Manual testing

## Checklist
- [ ] My code follows the code style of this project
- [ ] I have commented my code where necessary
- [ ] I have added tests that prove my fix/feature works
- [ ] New and existing tests pass locally
- [ ] I have updated the documentation accordingly
```

### Step 6: Code Review

**What happens next:**
1. Maintainer reviews your PR (usually within 24 hours)
2. They may request changes
3. Make requested changes and push again
4. Once approved, PR is merged! 🎉

**Tips for faster review:**
- Keep PRs small (<300 lines)
- Write clear commit messages
- Add tests
- Update documentation
- Respond to feedback quickly

---

## 🎨 Code Style Guide

### C# (.NET)

```csharp
// ✅ Good
public async Task<User> GetUserAsync(Guid userId, CancellationToken cancellationToken = default)
{
    var user = await _dbContext.Users
        .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

    if (user == null)
    {
        throw new NotFoundException($"User {userId} not found");
    }

    return user;
}

// ❌ Bad
public User GetUser(Guid userId){
var user=_dbContext.Users.FirstOrDefault(u=>u.Id==userId);
if(user==null)throw new Exception("Not found");
return user;}
```

**Rules:**
- Use `async`/`await` for I/O operations
- Always pass `CancellationToken`
- Use explicit types (not `var` everywhere)
- Follow Microsoft naming conventions
- Add XML comments for public APIs

### TypeScript (React/Vue)

```typescript
// ✅ Good
interface LoginProps {
  onSuccess: (user: User) => void;
  onError: (error: string) => void;
}

export function Login({ onSuccess, onError }: LoginProps) {
  const [isLoading, setIsLoading] = useState(false);

  const handleLogin = async () => {
    setIsLoading(true);
    try {
      const user = await auth.login();
      onSuccess(user);
    } catch (error) {
      onError(error.message);
    } finally {
      setIsLoading(false);
    }
  };

  return <button onClick={handleLogin}>Login</button>;
}

// ❌ Bad
export function Login(props) {
  const handleLogin = () => {
    auth.login().then(props.onSuccess).catch(props.onError);
  };
  return <button onClick={handleLogin}>Login</button>;
}
```

**Rules:**
- Use TypeScript (not JavaScript)
- Define interfaces for props
- Use async/await (not `.then()`)
- Handle errors explicitly
- Use functional components (not class components)

---

## 🐛 Reporting Bugs

**Before reporting:**
1. Check existing issues
2. Try latest version
3. Read troubleshooting guide

**Bug Report Template:**
```markdown
**Describe the bug**
A clear description of what the bug is

**To Reproduce**
Steps to reproduce:
1. Go to '...'
2. Click on '...'
3. See error

**Expected behavior**
What you expected to happen

**Actual behavior**
What actually happened

**Screenshots**
If applicable, add screenshots

**Environment**
- OS: [e.g. macOS 14.0]
- Browser: [e.g. Chrome 120]
- IAM System version: [e.g. 1.0.0]
- Node.js version: [e.g. 20.10.0]
- .NET version: [e.g. 9.0.0]

**Additional context**
Any other relevant information
```

---

## 💡 Suggesting Features

**Feature Request Template:**
```markdown
**Problem**
Describe the problem this feature would solve

**Proposed Solution**
Describe how you'd like this to work

**Alternatives**
Other solutions you've considered

**Why This Matters**
Who benefits? How many users?

**Example Use Case**
Real-world scenario where this is needed
```

---

## 🔒 Security Issues

**DO NOT** create public issues for security vulnerabilities.

Instead, email: [security@iam.dev](mailto:security@iam.dev)

Include:
- Description of vulnerability
- Steps to reproduce
- Potential impact
- Suggested fix (if you have one)

**Bug Bounty:** We pay up to $10,000 for critical vulnerabilities.

See [SECURITY.md](./SECURITY.md) for details.

---

## 🌍 Translations

We need help translating into:
- 🇫🇷 French
- 🇩🇪 German
- 🇪🇸 Spanish
- 🇳🇱 Dutch
- 🇯🇵 Japanese
- 🇨🇳 Chinese
- 🇧🇷 Portuguese
- 🇷🇺 Russian

**Translation Guide:**
1. Copy `locales/en-US.json`
2. Create `locales/YOUR_LOCALE.json`
3. Translate all strings
4. Test in browser
5. Submit PR

**Example:**
```json
{
  "auth.login": "Login",
  "auth.logout": "Logout",
  "auth.register": "Register"
}
```

---

## 📊 Recognition

**Contributors are recognized in:**
- README.md contributors section
- Release notes
- Annual report
- Discord "Contributors" role

**Top contributors get:**
- Early access to new features
- Direct line to maintainers
- Priority support for their projects
- Invitation to private contributor Discord channel

---

## 📧 Communication

### Discord

Join our Discord: [discord.gg/iam-system](https://discord.gg/iam-system)

**Channels:**
- `#general` - General discussion
- `#help` - Get help using IAM System
- `#contributors` - Contribute to development
- `#feature-requests` - Suggest features
- `#showcase` - Show what you built

### GitHub Discussions

For longer-form discussions: [github.com/martiendejong/iam-system/discussions](https://github.com/martiendejong/iam-system/discussions)

**Categories:**
- 💡 Ideas - Feature suggestions
- 🙏 Q&A - Questions
- 📣 Show and tell - Your projects
- 📚 Guides - Tutorials

---

## 📜 Code of Conduct

See [CODE_OF_CONDUCT.md](./CODE_OF_CONDUCT.md)

**TL;DR:**
- Be respectful
- Be welcoming
- Be constructive
- No harassment
- No spam

---

## 📄 License

By contributing, you agree that your contributions will be licensed under the MIT License.

---

## 🙏 Thank You

Every contribution matters. Whether it's:
- A single-line typo fix
- A major feature
- Answering a question on Discord
- Sharing IAM System with a friend

**You're helping rescue developers from Auth0 pricing traps.** 💪

**Welcome to the revolution.** 🚀
