// ===== Cost Calculator =====
const mauSlider = document.getElementById('mau');
const mauDisplay = document.getElementById('mau-display');
const auth0Cost = document.getElementById('auth0-cost');
const azureCost = document.getElementById('azure-cost');
const iamCost = document.getElementById('iam-cost');
const savingsDisplay = document.getElementById('savings');

function formatCurrency(amount) {
    return new Intl.NumberFormat('en-US', {
        style: 'currency',
        currency: 'USD',
        maximumFractionDigits: 0
    }).format(amount);
}

function formatNumber(num) {
    if (num >= 1000000) {
        return (num / 1000000).toFixed(1) + 'M';
    } else if (num >= 1000) {
        return (num / 1000).toFixed(0) + 'K';
    }
    return num.toString();
}

function calculateCosts(mau) {
    // Auth0 Pricing (based on real 2026 data)
    // Free: 7,500 MAU
    // Essentials: $35/month for 10K MAU, then $2.00 per 100 MAU
    // Professional: $240/month for 10K MAU, then $2.40 per 100 MAU
    // Enterprise: Custom pricing (typically 3-4x Professional)

    let auth0;
    if (mau <= 7500) {
        auth0 = 0;
    } else if (mau <= 10000) {
        auth0 = 35 * 12; // $420/year
    } else if (mau <= 100000) {
        // Professional tier for reliability
        const baseCost = 240 * 12; // $2,880/year
        const extraMAU = mau - 10000;
        const extraCost = (extraMAU / 100) * 2.40 * 12;
        auth0 = baseCost + extraCost;
    } else {
        // Enterprise (3x multiplier)
        const baseCost = 240 * 12;
        const extraMAU = mau - 10000;
        const extraCost = (extraMAU / 100) * 2.40 * 12 * 3;
        auth0 = baseCost + extraCost;
    }

    // Azure AD B2C Pricing
    // First 50K MAU: Free
    // 50K-100K MAU: $0.00325 per MAU
    // 100K+ MAU: $0.0016 per MAU

    let azure;
    if (mau <= 50000) {
        azure = 0;
    } else if (mau <= 100000) {
        azure = (mau - 50000) * 0.00325 * 12;
    } else {
        azure = (50000 * 0.00325 * 12) + ((mau - 100000) * 0.0016 * 12);
    }

    // IAM System Pricing
    // Free: 10K MAU
    // Pro: $0.01 per successful login
    // Assuming 5 logins per user per month average

    let iam;
    if (mau <= 10000) {
        iam = 0;
    } else {
        const paidMAU = mau - 10000;
        const logins = paidMAU * 5; // 5 logins/user/month
        iam = logins * 0.01 * 12; // $0.01 per login
    }

    return { auth0, azure, iam };
}

function updateCalculator() {
    const mau = parseInt(mauSlider.value);
    mauDisplay.textContent = formatNumber(mau);

    const costs = calculateCosts(mau);

    auth0Cost.textContent = formatCurrency(costs.auth0) + '/year';
    azureCost.textContent = formatCurrency(costs.azure) + '/year';
    iamCost.textContent = formatCurrency(costs.iam) + '/year';

    const savings = costs.auth0 - costs.iam;
    savingsDisplay.textContent = formatCurrency(savings);
}

if (mauSlider) {
    mauSlider.addEventListener('input', updateCalculator);
    updateCalculator(); // Initial calculation
}

// ===== Quickstart Tabs =====
const tabs = document.querySelectorAll('.tab');
const tabContents = document.querySelectorAll('.quickstart-tab-content');

tabs.forEach(tab => {
    tab.addEventListener('click', () => {
        // Remove active class from all tabs
        tabs.forEach(t => t.classList.remove('active'));
        tabContents.forEach(content => content.classList.remove('active'));

        // Add active class to clicked tab
        tab.classList.add('active');

        // Show corresponding content
        const targetTab = tab.getAttribute('data-tab');
        const targetContent = document.getElementById(targetTab);
        if (targetContent) {
            targetContent.classList.add('active');
        }
    });
});

// ===== Mobile Menu Toggle =====
const mobileMenuToggle = document.querySelector('.mobile-menu-toggle');
const navLinks = document.querySelector('.nav-links');

if (mobileMenuToggle) {
    mobileMenuToggle.addEventListener('click', () => {
        navLinks.classList.toggle('active');
        mobileMenuToggle.classList.toggle('active');
    });
}

// ===== Smooth Scroll =====
document.querySelectorAll('a[href^="#"]').forEach(anchor => {
    anchor.addEventListener('click', function (e) {
        e.preventDefault();
        const target = document.querySelector(this.getAttribute('href'));
        if (target) {
            target.scrollIntoView({
                behavior: 'smooth',
                block: 'start'
            });
        }
    });
});

// ===== Terminal Animation =====
const terminalLines = document.querySelectorAll('.terminal-line');
let delay = 0;

terminalLines.forEach((line, index) => {
    line.style.opacity = '0';
    line.style.transform = 'translateX(-10px)';

    setTimeout(() => {
        line.style.transition = 'all 0.3s ease-out';
        line.style.opacity = '1';
        line.style.transform = 'translateX(0)';
    }, delay);

    delay += 300;
});

// ===== Intersection Observer for Animations =====
const observerOptions = {
    threshold: 0.1,
    rootMargin: '0px 0px -50px 0px'
};

const observer = new IntersectionObserver((entries) => {
    entries.forEach(entry => {
        if (entry.isIntersecting) {
            entry.target.style.opacity = '1';
            entry.target.style.transform = 'translateY(0)';
        }
    });
}, observerOptions);

// Animate cards on scroll
document.querySelectorAll('.problem-card, .solution-card, .feature, .testimonial').forEach(card => {
    card.style.opacity = '0';
    card.style.transform = 'translateY(20px)';
    card.style.transition = 'all 0.5s ease-out';
    observer.observe(card);
});

// ===== Copy Code Button (for quickstart examples) =====
const codeBlocks = document.querySelectorAll('pre code');

codeBlocks.forEach(codeBlock => {
    const pre = codeBlock.parentElement;
    const button = document.createElement('button');
    button.className = 'copy-button';
    button.innerHTML = `
        <svg width="16" height="16" viewBox="0 0 16 16" fill="currentColor">
            <path d="M4 2a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V2Z"/>
            <path d="M2 6a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2v-1H2V6Z"/>
        </svg>
        Copy
    `;

    pre.style.position = 'relative';
    button.style.position = 'absolute';
    button.style.top = '1rem';
    button.style.right = '1rem';
    button.style.padding = '0.5rem 1rem';
    button.style.background = 'rgba(255, 255, 255, 0.1)';
    button.style.border = 'none';
    button.style.borderRadius = '0.25rem';
    button.style.color = 'white';
    button.style.cursor = 'pointer';
    button.style.display = 'flex';
    button.style.alignItems = 'center';
    button.style.gap = '0.5rem';
    button.style.fontSize = '0.875rem';
    button.style.fontWeight = '500';
    button.style.transition = 'all 0.2s';

    button.addEventListener('click', async () => {
        const code = codeBlock.textContent;

        try {
            await navigator.clipboard.writeText(code);
            button.innerHTML = `
                <svg width="16" height="16" viewBox="0 0 16 16" fill="currentColor">
                    <path d="M13.78 4.22a.75.75 0 010 1.06l-7.25 7.25a.75.75 0 01-1.06 0L2.22 9.28a.75.75 0 011.06-1.06L6 10.94l6.72-6.72a.75.75 0 011.06 0z"/>
                </svg>
                Copied!
            `;
            button.style.background = 'rgba(16, 185, 129, 0.2)';

            setTimeout(() => {
                button.innerHTML = `
                    <svg width="16" height="16" viewBox="0 0 16 16" fill="currentColor">
                        <path d="M4 2a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V2Z"/>
                        <path d="M2 6a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2v-1H2V6Z"/>
                    </svg>
                    Copy
                `;
                button.style.background = 'rgba(255, 255, 255, 0.1)';
            }, 2000);
        } catch (err) {
            console.error('Failed to copy code:', err);
        }
    });

    button.addEventListener('mouseenter', () => {
        button.style.background = 'rgba(255, 255, 255, 0.2)';
    });

    button.addEventListener('mouseleave', () => {
        if (!button.textContent.includes('Copied')) {
            button.style.background = 'rgba(255, 255, 255, 0.1)';
        }
    });

    pre.appendChild(button);
});

// ===== Stats Counter Animation =====
function animateCounter(element, target) {
    const duration = 2000;
    const start = 0;
    const increment = target / (duration / 16);
    let current = start;

    const timer = setInterval(() => {
        current += increment;
        if (current >= target) {
            current = target;
            clearInterval(timer);
        }
        element.textContent = Math.floor(current).toLocaleString();
    }, 16);
}

const statsObserver = new IntersectionObserver((entries) => {
    entries.forEach(entry => {
        if (entry.isIntersecting) {
            const value = entry.target.textContent;
            if (value.includes('sec')) {
                animateCounter(entry.target, 60);
                entry.target.textContent += ' sec';
            }
            statsObserver.unobserve(entry.target);
        }
    });
}, { threshold: 0.5 });

document.querySelectorAll('.stat-value').forEach(stat => {
    if (stat.textContent === '60 sec') {
        // Don't animate text values
    }
});

// ===== Newsletter Form (if added later) =====
const newsletterForm = document.querySelector('.newsletter-form');
if (newsletterForm) {
    newsletterForm.addEventListener('submit', async (e) => {
        e.preventDefault();
        const email = newsletterForm.querySelector('input[type="email"]').value;

        // TODO: Implement newsletter signup API call
        console.log('Newsletter signup:', email);

        newsletterForm.innerHTML = '<p class="success">✓ Thanks for subscribing!</p>';
    });
}

// ===== Analytics Event Tracking =====
function trackEvent(category, action, label) {
    if (typeof gtag !== 'undefined') {
        gtag('event', action, {
            'event_category': category,
            'event_label': label
        });
    }
}

// Track CTA clicks
document.querySelectorAll('.btn-primary').forEach(button => {
    button.addEventListener('click', () => {
        trackEvent('CTA', 'click', button.textContent.trim());
    });
});

// Track calculator usage
if (mauSlider) {
    mauSlider.addEventListener('change', () => {
        trackEvent('Calculator', 'adjust', `${mauSlider.value} MAU`);
    });
}

// Track demo clicks
document.querySelectorAll('a[href*="demo"]').forEach(link => {
    link.addEventListener('click', () => {
        trackEvent('Demo', 'click', link.href);
    });
});

// Track GitHub clicks
document.querySelectorAll('a[href*="github"]').forEach(link => {
    link.addEventListener('click', () => {
        trackEvent('GitHub', 'click', link.href);
    });
});

console.log('IAM System Landing Page - Loaded successfully ✓');
